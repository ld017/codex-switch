using System.Collections.Concurrent;
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
    private readonly RateLimitsFetcher _fetch;
    private readonly ITemporaryDirectoryCleaner _cleaner;
    private readonly string? _liveAuthPath;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _accountGates = new();

    public UsageFetcher(
        AccountStore accounts,
        string configPath,
        string temporaryRoot,
        RateLimitsFetcher fetch,
        ITemporaryDirectoryCleaner? cleaner = null,
        string? liveAuthPath = null)
    {
        _accounts = accounts;
        _configPath = configPath;
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _fetch = fetch;
        _cleaner = cleaner ?? new TemporaryDirectoryCleaner();
        _liveAuthPath = liveAuthPath;
    }

    public async Task<UsageSnapshot> FetchAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var gate = _accountGates.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await FetchIsolatedAsync(profileId, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<UsageSnapshot> FetchIsolatedAsync(Guid profileId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_temporaryRoot);
        try
        {
            await _cleaner.CleanStaleAsync(_temporaryRoot, TimeSpan.FromMinutes(5), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Stale temporary data must not prevent a fresh usage request.
        }

        var home = Path.Combine(_temporaryRoot, $"usage-{profileId:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        var keepHome = false;
        try
        {
            var originalAuth = await _accounts.LoadAuthAsync(profileId, cancellationToken);
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
            try { response = await _fetch(home, cancellationToken).ConfigureAwait(false); }
            finally
            {
                if (externalAuth is null)
                {
                    // OAuth may rotate credentials before the quota request fails or is cancelled.
                    // Persist that rotation before deleting the isolated home, even after cancellation.
                    var changedAuth = await File.ReadAllBytesAsync(Path.Combine(home, "auth.json"), CancellationToken.None).ConfigureAwait(false);
                    if (!changedAuth.SequenceEqual(originalAuth))
                    {
                        if (!AuthBlob.Parse(originalAuth).IdentityMatches(AuthBlob.Parse(changedAuth)))
                            throw new UsageException("Codex returned credentials for a different account.");
                        try
                        {
                            _ = await _accounts.TryReplaceAuthAsync(profileId, originalAuth, changedAuth, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Keep a valid rotated token available for recovery if the encrypted store fails.
                            keepHome = true;
                            await File.WriteAllTextAsync(Path.Combine(home, "auth-recovery-required"), string.Empty, CancellationToken.None).ConfigureAwait(false);
                            throw new UsageException($"无法保存已更新的登录凭据。隔离凭据保留于：{home}");
                        }
                    }
                }
            }

            return UsageSnapshotMapper.Map(response, DateTimeOffset.UtcNow);
        }
        finally
        {
            try
            {
                if (!keepHome) _ = await _cleaner.TryDeleteAsync(home, CancellationToken.None);
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
        if (!AuthBlob.Parse(storedAuth).IdentityMatches(AuthBlob.Parse(liveAuth))) return null;
        return ExternalUsageAuth.FromAuth(liveAuth);
    }
}
