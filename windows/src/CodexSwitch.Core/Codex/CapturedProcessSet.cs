namespace CodexSwitch.Core.Codex;

public sealed record CodexProcessCandidate(
    int Id,
    bool HasMainWindow,
    DateTimeOffset StartTime,
    int? ParentId = null,
    string? PackageFamilyName = null,
    string? ExecutablePath = null);

public sealed class CapturedProcessSet
{
    private readonly HashSet<int> _captured;
    private readonly Dictionary<int, DateTimeOffset> _instances = [];
    public CapturedProcessSet(IEnumerable<int> processIds) => _captured = processIds.ToHashSet();
    public IReadOnlyCollection<int> ProcessIds => _captured;
    public IReadOnlyCollection<int> RemainingFrom(IEnumerable<int> running) => running.Where(_captured.Contains).ToArray();

    public IReadOnlyCollection<int> RemainingInstancesFrom(IEnumerable<CodexProcessCandidate> running)
        => running.Where(candidate => _captured.Contains(candidate.Id)
            && (!_instances.TryGetValue(candidate.Id, out var started)
                || started == DateTimeOffset.MaxValue || candidate.StartTime == DateTimeOffset.MaxValue
                || started == candidate.StartTime))
            .Select(candidate => candidate.Id).ToArray();

    public bool MatchesKnownInstance(CodexProcessCandidate candidate)
        => _captured.Contains(candidate.Id)
            && _instances.TryGetValue(candidate.Id, out var started)
            && started != DateTimeOffset.MaxValue
            && candidate.StartTime != DateTimeOffset.MaxValue
            && started == candidate.StartTime;

    public static IReadOnlyList<CodexProcessCandidate> SelectDesktopProcesses(IEnumerable<CodexProcessCandidate> candidates)
        => candidates.Where(candidate =>
            string.Equals(candidate.PackageFamilyName, "OpenAI.Codex_2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase)
            && candidate.ExecutablePath is string path
            && (path.Replace('/', '\\').EndsWith(@"\app\ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                || path.Replace('/', '\\').EndsWith(@"\app\Codex.exe", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    public static CapturedProcessSet CaptureTree(IEnumerable<CodexProcessCandidate> candidates, IEnumerable<int> rootIds)
    {
        var captured = new CapturedProcessSet(rootIds);
        captured.IncludeDescendants(candidates);
        return captured;
    }

    // Include children created while the desktop is shutting down. Creation times prevent PID reuse
    // from turning an unrelated process into a captured child.
    public void IncludeDescendants(IEnumerable<CodexProcessCandidate> candidates, bool requirePresentParent = false)
    {
        var snapshot = candidates.ToArray();
        foreach (var candidate in snapshot.Where(candidate => _captured.Contains(candidate.Id)))
        {
            _instances.TryAdd(candidate.Id, candidate.StartTime);
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var candidate in snapshot)
            {
                if (candidate.ParentId is not int parentId || !_captured.Contains(parentId)) continue;
                var parent = snapshot.FirstOrDefault(item => item.Id == parentId);
                if (requirePresentParent && parent is null) continue;
                if (!_instances.TryGetValue(parentId, out var started)) continue;
                if (requirePresentParent && (started == DateTimeOffset.MaxValue
                    || parent!.StartTime == DateTimeOffset.MaxValue
                    || candidate.StartTime == DateTimeOffset.MaxValue)) continue;
                if (started != DateTimeOffset.MaxValue
                    && ((parent is not null && parent.StartTime != DateTimeOffset.MaxValue && parent.StartTime != started)
                        || (candidate.StartTime != DateTimeOffset.MaxValue && candidate.StartTime < started))) continue;
                if (_captured.Add(candidate.Id))
                {
                    _instances[candidate.Id] = candidate.StartTime;
                    changed = true;
                }
            }
        } while (changed);
    }

    public static CapturedProcessSet SelectRoots(IEnumerable<CodexProcessCandidate> candidates)
    {
        var snapshot = candidates.ToArray();
        var withWindows = snapshot.Where(candidate => candidate.HasMainWindow).Select(candidate => candidate.Id).ToArray();
        if (withWindows.Length > 0)
        {
            return new CapturedProcessSet(withWindows);
        }

        var oldest = snapshot.OrderBy(candidate => candidate.StartTime).FirstOrDefault();
        return new CapturedProcessSet(oldest is null ? [] : [oldest.Id]);
    }
}
