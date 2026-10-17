using System.Text.Json;
using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Usage;

public sealed class UsageCache
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly IAtomicFileStore _files;

    public UsageCache(string path, IAtomicFileStore files) { _path = path; _files = files; }

    public Task SaveAsync(IReadOnlyDictionary<Guid, UsageState> states, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bounded = states.ToDictionary(pair => pair.Key, pair => TokenUsageRetention.Normalize(pair.Value));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(bounded, Options);
        if (bytes.Length <= MaximumBytes) _files.WriteAtomically(_path, bytes);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Guid, UsageState>> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = !_files.Exists(_path) ? new Dictionary<Guid, UsageState>()
                : JsonSerializer.Deserialize<Dictionary<Guid, UsageState>>(_files.ReadBounded(_path, MaximumBytes), Options)
                  ?? new Dictionary<Guid, UsageState>();
            return Task.FromResult<IReadOnlyDictionary<Guid, UsageState>>(result
                .Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => TokenUsageRetention.Normalize(pair.Value)));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // This file is rebuildable. Never apply this policy to the encrypted account store.
            return Task.FromResult<IReadOnlyDictionary<Guid, UsageState>>(new Dictionary<Guid, UsageState>());
        }
    }
}
