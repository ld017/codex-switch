namespace CodexSwitch.Core.Auth;

public interface ICredentialProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}
