using System.Text;
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

    public UsageFetcher(
        AccountStore accounts,
        string configPath,
        string temporaryRoot,
        RateLimitsFetcher fetch,
        ITemporaryDirectoryCleaner? cleaner = null)
    {
        _accounts = accounts;
        _configPath = configPath;
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _fetch = fetch;
        _cleaner = cleaner ?? new TemporaryDirectoryCleaner();
    }

    public async Task<UsageSnapshot> FetchAsync(Guid profileId, CancellationToken cancellationToken)
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
        try
        {
            var originalAuth = await _accounts.LoadAuthAsync(profileId, cancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(home, "auth.json"), originalAuth, cancellationToken);
            var source = File.Exists(_configPath) ? await File.ReadAllTextAsync(_configPath, cancellationToken) : "model = \"gpt-5\"\n";
            await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), OpenAiUsageConfig.Force(source), Utf8NoBom, cancellationToken);
            var response = await _fetch(home, cancellationToken);

            var changedAuth = await File.ReadAllBytesAsync(Path.Combine(home, "auth.json"), cancellationToken);
            if (!changedAuth.SequenceEqual(originalAuth))
            {
                var oldBlob = AuthBlob.Parse(originalAuth);
                var newBlob = AuthBlob.Parse(changedAuth);
                if (!oldBlob.IdentityMatches(newBlob)) throw new UsageException("Codex returned credentials for a different account.");
                await _accounts.ReplaceAuthAsync(profileId, changedAuth, cancellationToken);
            }

            return UsageSnapshotMapper.Map(response, DateTimeOffset.UtcNow);
        }
        finally
        {
            try
            {
                _ = await _cleaner.TryDeleteAsync(home, CancellationToken.None);
            }
            catch
            {
                // A delayed Windows file-handle release must not replace valid usage data.
            }
        }
    }
}
