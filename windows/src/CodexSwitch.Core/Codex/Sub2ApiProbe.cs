using System.Net.Sockets;

namespace CodexSwitch.Core.Codex;

public sealed class Sub2ApiProbe
{
    private readonly int _port;
    private readonly TimeSpan _timeout;
    public Sub2ApiProbe(int port = 8080, TimeSpan? timeout = null) { _port = port; _timeout = timeout ?? TimeSpan.FromSeconds(2); }

    public async Task<bool> IsOnlineAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var client = new TcpClient();
        try { await client.ConnectAsync("127.0.0.1", _port, timeout.Token); return true; }
        catch (Exception error) when (error is SocketException or OperationCanceledException) { return false; }
    }
}
