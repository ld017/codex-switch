using System.Text;
using CodexSwitch.App.Windows;

namespace CodexSwitch.IntegrationTests.Windows;

public sealed class DpapiCredentialProtectorTests
{
    [Fact]
    public void Protect_and_unprotect_round_trip_for_current_user()
    {
        var protector = new DpapiCredentialProtector();
        var plaintext = Encoding.UTF8.GetBytes("credential-value");

        var protectedBytes = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, protectedBytes);
        Assert.Equal(plaintext, protector.Unprotect(protectedBytes));
    }
}
