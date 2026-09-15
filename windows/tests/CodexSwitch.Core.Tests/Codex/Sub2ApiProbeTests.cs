using System.Net;
using System.Net.Sockets;
using CodexSwitch.Core.Codex;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class Sub2ApiProbeTests
{
    [Fact]
    public async Task IsOnlineAsync_returns_true_when_loopback_port_accepts_connections()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var probe = new Sub2ApiProbe(port, TimeSpan.FromSeconds(2));

        Assert.True(await probe.IsOnlineAsync(default));
    }

    [Fact]
    public async Task IsOnlineAsync_returns_false_when_port_is_closed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var probe = new Sub2ApiProbe(port, TimeSpan.FromMilliseconds(250));

        Assert.False(await probe.IsOnlineAsync(default));
    }
}
