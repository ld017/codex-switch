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
    private readonly TimeSpan _timeout;
    private int _nextId = 1;

    public CodexRpcClient(string executable, IReadOnlyDictionary<string, string?> environment, IReadOnlyList<string>? argumentPrefix = null, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
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
        start.ArgumentList.Add("-s"); start.ArgumentList.Add("read-only"); start.ArgumentList.Add("app-server");
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        _process = Process.Start(start) ?? throw new CodexRpcException("Failed to start Codex CLI.");
    }

    public async Task InitializeAsync(string clientName, string clientVersion, CancellationToken cancellationToken)
    {
        _ = await RequestAsync("initialize", new { clientInfo = new { name = clientName, version = clientVersion } }, cancellationToken);
        await SendAsync(new { jsonrpc = "2.0", method = "initialized", @params = new { } }, cancellationToken);
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

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = _nextId++;
        await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        while (true)
        {
            string? line;
            try { line = await _process.StandardOutput.ReadLineAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new CodexRpcException($"Codex CLI timed out on {method}."); }
            if (line is null)
            {
                var stderr = await _process.StandardError.ReadToEndAsync(cancellationToken);
                throw new CodexRpcException("Codex app-server closed stdout: " + stderr);
            }
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var messageId) || messageId.GetInt32() != id) continue;
                if (root.TryGetProperty("error", out var error))
                    throw new CodexRpcException(error.TryGetProperty("message", out var message) ? message.GetString() ?? "RPC error" : "RPC error");
                if (!root.TryGetProperty("result", out var result)) throw new CodexRpcException("RPC response has no result.");
                return result.Clone();
            }
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
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process.Dispose();
    }
}
