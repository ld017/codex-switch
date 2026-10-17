namespace CodexSwitch.Core.Presentation;

/// <summary>UI-thread state for one refresh drain and one coalesced forced follow-up.</summary>
public sealed class RefreshRequestScheduler
{
    private bool _blocked;
    private bool _closed;
    public bool IsRunning { get; private set; }
    public bool HasPendingForcedRequest { get; private set; }

    public bool TryStart(bool forceTokens)
    {
        if (_blocked || _closed) return false;
        if (IsRunning)
        {
            HasPendingForcedRequest |= forceTokens;
            return false;
        }
        IsRunning = true;
        return true;
    }

    /// <returns>Whether the caller owns another forced round after the completed round's cleanup.</returns>
    public bool CompleteRound()
    {
        if (IsRunning && HasPendingForcedRequest && !_blocked && !_closed)
        {
            HasPendingForcedRequest = false;
            return true;
        }
        Abandon();
        return false;
    }

    public void SetBlocked(bool blocked)
    {
        _blocked = blocked;
        if (blocked) HasPendingForcedRequest = false;
    }

    public void Close()
    {
        _closed = true;
        HasPendingForcedRequest = false;
    }

    public void Abandon()
    {
        IsRunning = false;
        HasPendingForcedRequest = false;
    }
}
