using System.Text.Json;
using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Usage;

public sealed class UsageCache
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly IAtomicFileStore _files;

    public UsageCache(string path, IAtomicFileStore files) { _path = path; _files = files; }

    public Task SaveAsync(IReadOnlyDictionary<Guid, UsageState> states, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _files.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(states, Options));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Guid, UsageState>> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<Guid, UsageState> result = !_files.Exists(_path)
            ? new Dictionary<Guid, UsageState>()
            : JsonSerializer.Deserialize<Dictionary<Guid, UsageState>>(_files.ReadAllBytes(_path), Options)
              ?? new Dictionary<Guid, UsageState>();
        return Task.FromResult(result);
    }
}
