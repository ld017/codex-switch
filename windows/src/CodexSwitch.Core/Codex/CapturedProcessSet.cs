namespace CodexSwitch.Core.Codex;

public sealed class CapturedProcessSet
{
    private readonly HashSet<int> _captured;
    public CapturedProcessSet(IEnumerable<int> processIds) => _captured = processIds.ToHashSet();
    public IReadOnlyCollection<int> ProcessIds => _captured;
    public IReadOnlyCollection<int> RemainingFrom(IEnumerable<int> running) => running.Where(_captured.Contains).ToArray();
}
