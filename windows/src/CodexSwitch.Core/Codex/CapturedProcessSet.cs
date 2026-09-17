namespace CodexSwitch.Core.Codex;

public sealed record CodexProcessCandidate(int Id, bool HasMainWindow, DateTimeOffset StartTime);

public sealed class CapturedProcessSet
{
    private readonly HashSet<int> _captured;
    public CapturedProcessSet(IEnumerable<int> processIds) => _captured = processIds.ToHashSet();
    public IReadOnlyCollection<int> ProcessIds => _captured;
    public IReadOnlyCollection<int> RemainingFrom(IEnumerable<int> running) => running.Where(_captured.Contains).ToArray();

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
