using System.Text;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Tests.Auth;

public sealed class AccountStoreTests
{
    [Fact]
    public async Task SaveAsync_never_persists_plaintext_token()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.Path, new PrefixProtector(), new AtomicFileStore());
        var auth = TestAuth.OAuth("secret-access-token", "secret-refresh-token", "acct-1");

        var profile = await store.SaveAsync("Work", auth, default);

        var encryptedPath = System.IO.Path.Combine(directory.Path, "accounts", $"{profile.Id:N}.bin");
        var diskText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(encryptedPath));
        Assert.DoesNotContain("secret-access-token", diskText);
        Assert.StartsWith("protected:", diskText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_load_rename_and_remove_round_trip()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.Path, new PrefixProtector(), new AtomicFileStore());
        var auth = TestAuth.OAuth("access", "refresh", "acct-1");

        var profile = await store.SaveAsync("Work", auth, default);
        Assert.Equal(auth, await store.LoadAuthAsync(profile.Id, default));

        await store.RenameAsync(profile.Id, "Personal", default);
        Assert.Equal("Personal", (await store.ListAsync(default)).Single().Label);

        await store.RemoveAsync(profile.Id, default);
        Assert.Empty(await store.ListAsync(default));
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "accounts", $"{profile.Id:N}.bin")));
    }

    private sealed class PrefixProtector : ICredentialProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Encoding.UTF8.GetBytes("protected:" + Convert.ToBase64String(plaintext));

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        {
            var value = Encoding.UTF8.GetString(ciphertext);
            return Convert.FromBase64String(value["protected:".Length..]);
        }
    }
}
