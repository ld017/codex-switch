namespace CodexSwitch.Core.Presentation;

public sealed class OperationCoordinator
{
    private int _busy;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public async Task<bool> TryRunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        try { await operation(cancellationToken); return true; }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
