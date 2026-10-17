using System.Text;
using System.Text.Json;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Usage;

public delegate Task<RateLimitsResponse> RateLimitsFetcher(string isolatedCodexHome, CancellationToken cancellationToken);

public sealed class UsageFetcher
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private readonly AccountStore _accounts;
    private readonly string _configPath;
    private readonly string _temporaryRoot;
    private readonly StreamingRateLimitsFetcher _fetch;
    private readonly ITemporaryDirectoryCleaner _cleaner;
    private readonly string? _liveAuthPath;
    private readonly object _accountGateLock = new();
    private readonly Dictionary<Guid, AccountGate> _accountGates = [];

    private sealed class AccountGate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    public UsageFetcher(
        AccountStore accounts,
        string configPath,
        string temporaryRoot,
        RateLimitsFetcher fetch,
        ITemporaryDirectoryCleaner? cleaner = null,
        string? liveAuthPath = null)
        : this(accounts, configPath, temporaryRoot, (home, _, _, token) => fetch(home, token), cleaner, liveAuthPath) { }

    public UsageFetcher(AccountStore accounts, string configPath, string temporaryRoot,
        StreamingRateLimitsFetcher fetch, ITemporaryDirectoryCleaner? cleaner = null, string? liveAuthPath = null)
    {
        _accounts = accounts;
        _configPath = configPath;
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _fetch = fetch;
        _cleaner = cleaner ?? new TemporaryDirectoryCleaner();
        _liveAuthPath = liveAuthPath;
    }

    public async Task<UsageSnapshot> FetchAsync(Guid profileId, CancellationToken cancellationToken,
        UsageFetchOptions? options = null, Action<UsageSnapshot>? quotaReceived = null)
    {
        AccountGate gate;
        lock (_accountGateLock)
        {
            if (!_accountGates.TryGetValue(profileId, out gate!)) _accountGates.Add(profileId, gate = new());
            gate.Users++;
        }
        var acquired = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            return await FetchIsolatedAsync(profileId, cancellationToken, options ?? new(), quotaReceived).ConfigureAwait(false);
        }
        finally
        {
            if (acquired) gate.Semaphore.Release();
            lock (_accountGateLock)
            {
                // Include waiters in Users so cancellation cannot retire another caller's lock.
                if (--gate.Users == 0)
                {
                    _accountGates.Remove(profileId);
                    gate.Semaphore.Dispose();
                }
            }
        }
    }

    private async Task<UsageSnapshot> FetchIsolatedAsync(Guid profileId, CancellationToken cancellationToken,
        UsageFetchOptions options, Action<UsageSnapshot>? quotaReceived)
    {
        Directory.CreateDirectory(_temporaryRoot);
        var home = Path.Combine(_temporaryRoot, $"usage-{profileId:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        using var lease = TemporaryHomeLease.Create(home);
        var safeToDelete = false;
        var requestStarted = false;
        byte[]? originalAuth = null;
        try
        {
            originalAuth = await _accounts.LoadAuthAsync(profileId, cancellationToken);
            var externalAuth = await ReadLiveCredentialsAsync(profileId, originalAuth, cancellationToken).ConfigureAwait(false);
            if (externalAuth is not null)
            {
                // Never give an isolated CLI the refresh token that a running Codex owns.
                // The RPC caller explicitly installs these access-only credentials after initialize.
                await File.WriteAllBytesAsync(Path.Combine(home, ExternalUsageAuth.FileName),
                    JsonSerializer.SerializeToUtf8Bytes(externalAuth, ExternalUsageAuth.JsonOptions), cancellationToken);
            }
            else
            {
                await File.WriteAllBytesAsync(Path.Combine(home, "auth.json"), originalAuth, cancellationToken);
            }
            var source = File.Exists(_configPath) ? await File.ReadAllTextAsync(_configPath, cancellationToken) : "model = \"gpt-5\"\n";
            await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), OpenAiUsageConfig.Force(source), Utf8NoBom, cancellationToken);
            RateLimitsResponse response;
            DateTimeOffset? quotaDate = null;
            try
            {
                requestStarted = true;
                response = await _fetch(home, options, (quotas, fetchedAt) =>
                {
                    quotaDate = fetchedAt;
                    try { quotaReceived?.Invoke(UsageSnapshotMapper.Map(quotas, fetchedAt)); }
                    catch { /* Observers cannot interrupt credential persistence. */ }
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (externalAuth is null)
                {
                    // OAuth may rotate credentials before the quota request fails or is cancelled.
                    // Persist that rotation before deleting the isolated home, even after cancellation.
                    try
                    {
                        var changedAuth = await File.ReadAllBytesAsync(Path.Combine(home, "auth.json"), CancellationToken.None).ConfigureAwait(false);
                        try
                        {
                            if (!changedAuth.SequenceEqual(originalAuth))
                            {
                                if (!AuthBlob.Parse(originalAuth).IdentityMatches(AuthBlob.Parse(changedAuth)))
                                    throw new UsageException("Codex returned credentials for a different account.");
                                // A false CAS means a newer explicit login owns the encrypted store.
                                _ = await _accounts.TryReplaceAuthAsync(profileId, originalAuth, changedAuth, CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(changedAuth); }
                    }
                    catch
                    {
                        lease.MarkRecoveryRequired();
                        throw new UsageException($"无法保存或验证已更新的登录凭据。隔离凭据保留于：{home}");
                    }
                }
                // External auth contains only the desktop-owned access token; no rotation is owned here.
                if (TemporaryDirectoryCleaner.HasCredentialFiles(home, ["auth.json", ExternalUsageAuth.FileName]))
                {
                    lease.MarkRecoveryRequired();
                    throw new UsageException($"发现未完成写入的隔离凭据，已保留以供恢复：{home}");
                }
                safeToDelete = true;
                lease.MarkCleanupReady();
            }

            var snapshot = UsageSnapshotMapper.Map(response, response.QuotasFetchedAt ?? quotaDate ?? DateTimeOffset.UtcNow);
            return snapshot.TokenUsage?.Snapshot is { } tokens
                ? snapshot with { TokenUsage = snapshot.TokenUsage with { Snapshot = TokenUsageRetention.Normalize(tokens) } }
                : snapshot;
        }
        finally
        {
            if (originalAuth is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(originalAuth);
            if (!requestStarted)
            {
                // No CLI has owned this home, so no credential rotation can be pending.
                lease.MarkCleanupReady();
                safeToDelete = true;
            }
            // Release the lease before retrying deletion, but never declare uncertain credentials disposable.
            lease.Dispose();
            try
            {
                var hasCredentials = File.Exists(Path.Combine(home, "auth.json")) || File.Exists(Path.Combine(home, ExternalUsageAuth.FileName));
                if (safeToDelete || !hasCredentials) _ = await _cleaner.TryDeleteAsync(home, CancellationToken.None);
            }
            catch
            {
                // A delayed Windows file-handle release must not replace valid usage data.
            }
        }
    }

    private async Task<ExternalUsageAuth?> ReadLiveCredentialsAsync(Guid profileId, byte[] storedAuth, CancellationToken cancellationToken)
    {
        if (_liveAuthPath is null) return null;
        byte[] liveAuth;
        try { liveAuth = await File.ReadAllBytesAsync(_liveAuthPath, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            if (await _accounts.GetActiveProfileIdAsync(cancellationToken).ConfigureAwait(false) == profileId)
                throw new UsageException("Authentication required: current Codex credentials are unavailable. Please sign in again.");
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new UsageException("Unable to read current Codex credentials safely. Retry after Codex finishes updating its login.");
        }
        try
        {
            if (!AuthBlob.Parse(storedAuth).IdentityMatches(AuthBlob.Parse(liveAuth))) return null;
            return ExternalUsageAuth.FromAuth(liveAuth);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(liveAuth); }
    }
}
