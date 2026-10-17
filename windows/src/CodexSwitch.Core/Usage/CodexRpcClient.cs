using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexSwitch.Core.Diagnostics;

namespace CodexSwitch.Core.Usage;

public sealed class CodexRpcException : Exception
{
    public CodexRpcException(string message) : base(SecretRedactor.Redact(message)) { }
}

public sealed class CodexRpcClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly BoundedLineReader _stdout;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _stderrGate = new();
    private readonly Task _stderrTask;
    private bool _stderrTruncated;
    private int _nextId = 1;

    public CodexRpcClient(string executable, IReadOnlyDictionary<string, string?> environment, IReadOnlyList<string>? argumentPrefix = null, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in argumentPrefix ?? []) start.ArgumentList.Add(argument);
        // The isolated CODEX_HOME must not read or replace credentials in the global OS keyring.
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        start.ArgumentList.Add("-s"); start.ArgumentList.Add("read-only"); start.ArgumentList.Add("app-server");
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        start.Environment.Remove("OPENAI_API_KEY");
        start.Environment.Remove("CODEX_API_KEY");
        _process = Process.Start(start) ?? throw new CodexRpcException("Failed to start Codex CLI.");
        _stdout = new BoundedLineReader(_process.StandardOutput);
        _stderrTask = DrainStandardErrorAsync();
    }

    public async Task InitializeAsync(string clientName, string clientVersion, CancellationToken cancellationToken)
    {
        _ = await RequestAsync("initialize", new
        {
            clientInfo = new { name = clientName, version = clientVersion },
            capabilities = new { experimentalApi = true },
        }, cancellationToken).ConfigureAwait(false);
        await SendAsync(new { jsonrpc = "2.0", method = "initialized", @params = new { } }, cancellationToken);
    }

    public async Task UseExternalAuthFromHomeAsync(string isolatedCodexHome, CancellationToken cancellationToken)
    {
        var path = Path.Combine(isolatedCodexHome, ExternalUsageAuth.FileName);
        if (!File.Exists(path)) return;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var auth = JsonSerializer.Deserialize<ExternalUsageAuth>(bytes, ExternalUsageAuth.JsonOptions)
                   ?? throw new CodexRpcException("Authentication required: external usage credentials are unavailable.");
        if (string.IsNullOrWhiteSpace(auth.AccessToken) || string.IsNullOrWhiteSpace(auth.ChatgptAccountId))
            throw new CodexRpcException("Authentication required: external usage credentials are incomplete.");
        _ = await RequestAsync("account/login/start", new
        {
            type = "chatgptAuthTokens", accessToken = auth.AccessToken, chatgptAccountId = auth.ChatgptAccountId,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RateLimitsResponse> FetchRateLimitsAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("account/rateLimits/read", new { }, cancellationToken);
        try
        {
            return result.Deserialize<RateLimitsResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true })
                   ?? throw new JsonException("empty result");
        }
        catch (JsonException error) { throw new CodexRpcException("Malformed rate limit response: " + error.Message); }
    }

    public async Task<TokenUsageSnapshot> FetchTokenUsageAsync(CancellationToken cancellationToken)
    {
        // Keep this optional endpoint bounded separately from quota refreshes.
        var tokenTimeout = _timeout < TimeSpan.FromSeconds(5) ? _timeout : TimeSpan.FromSeconds(5);
        var result = await RequestAsync("account/usage/read", new { }, cancellationToken, tokenTimeout).ConfigureAwait(false);
        try
        {
            var response = result.Deserialize<TokenUsageResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("empty result");
            var summary = response.Summary is { } raw ? raw with
            {
                LifetimeTokens = raw.LifetimeTokens is >= 0 ? raw.LifetimeTokens : null,
                PeakDailyTokens = raw.PeakDailyTokens is >= 0 ? raw.PeakDailyTokens : null,
                ActiveDays = raw.ActiveDays is >= 0 ? raw.ActiveDays : null,
            } : null;
            var days = response.DailyUsageBuckets?
                .Where(day => day is { Tokens: >= 0 } && !string.IsNullOrWhiteSpace(day.StartDate))
                .Select(day => new TokenUsageDailyBucket(day!.StartDate!, day.Tokens!.Value)).ToArray();
            if (summary?.LifetimeTokens is null && summary?.PeakDailyTokens is null && days is not { Length: > 0 })
                throw new CodexRpcException("Codex 服务未返回 Token 用量，暂时无法获取。");
            return TokenUsageRetention.Normalize(new TokenUsageSnapshot(summary, days, DateTimeOffset.UtcNow));
        }
        catch (JsonException error) { throw new CodexRpcException("Malformed token usage response: " + error.Message); }
    }

    public async Task<RateLimitsResponse> FetchUsageAsync(CancellationToken cancellationToken, bool includeTokens = true,
        Action<RateLimitsResponse, DateTimeOffset>? quotaReceived = null)
    {
        var quotas = await FetchRateLimitsAsync(cancellationToken).ConfigureAwait(false);
        var quotasFetchedAt = DateTimeOffset.UtcNow;
        quotas = quotas with { QuotasFetchedAt = quotasFetchedAt };
        // Observers cannot prevent optional reads, credential persistence, or process cleanup.
        try { quotaReceived?.Invoke(quotas, quotasFetchedAt); }
        catch { }
        if (!includeTokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return quotas;
        }
        TokenUsageState tokens;
        try { tokens = TokenUsageState.Available(await FetchTokenUsageAsync(cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException error)
        {
            // The coordinator can distinguish its deadline from user cancellation;
            // the RPC client cannot. Carry the successful required read through
            // the fetcher's credential-rotation and temporary-home cleanup.
            throw new PartialUsageCancellationException(quotas, quotasFetchedAt, error);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Unsupported or failed activity reads must not throw away valid quota data.
            tokens = TokenUsageState.Failed(error.Message);
        }
        if (cancellationToken.IsCancellationRequested)
            throw new PartialUsageCancellationException(quotas, quotasFetchedAt, new OperationCanceledException(cancellationToken));
        return quotas with { TokenUsage = tokens };
    }

    private sealed record TokenUsageResponse(TokenUsageSummary? Summary, IReadOnlyList<TokenUsageBucketResponse?>? DailyUsageBuckets);
    private sealed record TokenUsageBucketResponse(string? StartDate, long? Tokens);

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var effectiveTimeout = requestTimeout ?? _timeout;
        timeout.CancelAfter(effectiveTimeout);
        try
        {
            var id = _nextId++;
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var line = await _stdout.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null)
                {
                    try { await _stderrTask.WaitAsync(TimeSpan.FromMilliseconds(100), timeout.Token).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                    throw new CodexRpcException($"Codex app-server closed stdout while handling {method}. {ReadStandardError()}");
                }
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    // Server requests have their own ID namespace. Reply immediately instead of waiting
                    // for a refresh timeout or ever rotating the active desktop account's refresh token.
                    if (root.TryGetProperty("method", out var serverMethod)
                        && serverMethod.ValueKind == JsonValueKind.String
                        && serverMethod.GetString() == "account/chatgptAuthTokens/refresh"
                        && root.TryGetProperty("id", out var serverId))
                    {
                        await SendAsync(new
                        {
                            jsonrpc = "2.0", id = serverId.Clone(),
                            error = new { code = -32001, message = "Authentication required: refresh this account in Codex before retrying usage." },
                        }, timeout.Token).ConfigureAwait(false);
                        throw new CodexRpcException("Authentication required: refresh this account in Codex before retrying usage.");
                    }
                    if (root.TryGetProperty("method", out _)) continue;
                    if (!root.TryGetProperty("id", out var messageId)
                        || messageId.ValueKind != JsonValueKind.Number || !messageId.TryGetInt32(out var responseId) || responseId != id) continue;
                    if (root.TryGetProperty("error", out var error))
                    {
                        var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var value)
                                      && value.ValueKind == JsonValueKind.String ? value.GetString() : error.GetRawText();
                        throw new CodexRpcException($"Codex RPC {method} failed: {message}");
                    }
                    if (!root.TryGetProperty("result", out var result)) throw new CodexRpcException($"RPC response for {method} has no result.");
                    return result.Clone();
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexRpcException($"Codex CLI timed out on {method} after {effectiveTimeout.TotalSeconds:0.#} seconds. {ReadStandardError()}");
        }
        catch (IOException error)
        {
            throw new CodexRpcException($"Codex CLI communication failed on {method}: {error.Message} {ReadStandardError()}");
        }
        finally { _requestGate.Release(); }
    }

    private async Task DrainStandardErrorAsync()
    {
        var buffer = new char[1024];
        try
        {
            while (true)
            {
                var count = await _process.StandardError.ReadAsync(buffer.AsMemory(), _shutdown.Token).ConfigureAwait(false);
                if (count == 0) return;
                lock (_stderrGate)
                {
                    _stderr.Append(buffer, 0, count);
                    if (_stderr.Length > 8192)
                    {
                        _stderr.Remove(0, _stderr.Length - 8192);
                        _stderrTruncated = true;
                    }
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private string ReadStandardError()
    {
        lock (_stderrGate)
        {
            var value = _stderr.ToString();
            // Never expose a trailing fragment of a token whose JSON key was truncated from the buffer.
            if (_stderrTruncated)
            {
                var firstLineEnd = value.IndexOf('\n');
                value = firstLineEnd < 0 ? string.Empty : value[(firstLineEnd + 1)..];
            }
            var redacted = SecretRedactor.Redact(value);
            return redacted.Length <= 1200 ? redacted : redacted[^1200..];
        }
    }

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload).AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try { _process.StandardInput.Close(); } catch (InvalidOperationException) { }
        try
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            _shutdown.Cancel();
            await _stderrTask.ConfigureAwait(false);
            _process.Dispose();
            _shutdown.Dispose();
            _requestGate.Dispose();
        }
    }
}
