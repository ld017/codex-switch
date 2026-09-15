using System.Security.Cryptography;
using System.Text;
using CodexSwitch.Core.Auth;

namespace CodexSwitch.App.Windows;

public sealed class DpapiCredentialProtector : ICredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexSwitch.Windows.Auth.v1");

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
        => ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        => ProtectedData.Unprotect(ciphertext.ToArray(), Entropy, DataProtectionScope.CurrentUser);
}
