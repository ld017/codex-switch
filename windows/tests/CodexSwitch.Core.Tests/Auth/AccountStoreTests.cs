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

    [Fact]
    public async Task RemoveAsync_rejects_active_account_when_another_account_exists()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.Path, new PrefixProtector(), new AtomicFileStore());
        var active = await store.SaveAsync("Active", TestAuth.OAuth("a1", "r1", "acct-1"), default);
        _ = await store.SaveAsync("Other", TestAuth.OAuth("a2", "r2", "acct-2"), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync(active.Id, default));

        Assert.Equal(2, (await store.ListAsync(default)).Count);
        Assert.Equal(active.Id, await store.GetActiveProfileIdAsync(default));
    }

    [Fact]
    public async Task ReconcileActiveProfileAsync_updates_stale_metadata_from_live_auth()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.Path, new PrefixProtector(), new AtomicFileStore());
        var firstAuth = TestAuth.OAuth("a1", "r1", "acct-1");
        var first = await store.SaveAsync("First", firstAuth, default);
        var second = await store.SaveAsync("Second", TestAuth.OAuth("a2", "r2", "acct-2"), default);
        await store.SetActiveProfileIdAsync(second.Id, default);

        var matched = await store.ReconcileActiveProfileAsync(firstAuth, default);

        Assert.Equal(first.Id, matched);
        Assert.Equal(first.Id, await store.GetActiveProfileIdAsync(default));
    }

    [Fact]
    public async Task ReconcileActiveProfileAsync_clears_active_when_live_auth_is_unmanaged()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.Path, new PrefixProtector(), new AtomicFileStore());
        _ = await store.SaveAsync("Managed", TestAuth.OAuth("a1", "r1", "acct-1"), default);

        var matched = await store.ReconcileActiveProfileAsync(TestAuth.OAuth("a2", "r2", "external"), default);

        Assert.Null(matched);
        Assert.Null(await store.GetActiveProfileIdAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaceAuth_synchronizes_fingerprint_when_token_rotation_adds_identity_claims(bool conditional)
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.Path, new PrefixProtector(), new AtomicFileStore());
        var original = TestAuth.OAuth("old", "old-refresh", "acct-1");
        var profile = await store.SaveAsync("Work", original, default);
        var updated = TestAuth.OAuth("new", "new-refresh", "acct-1", TestAuth.IdToken("subject", "mail@example.com"));

        if (conditional) Assert.True(await store.TryReplaceAuthAsync(profile.Id, original, updated, default));
        else await store.ReplaceAuthAsync(profile.Id, updated, default);

        Assert.Equal(updated, await store.LoadAuthAsync(profile.Id, default));
        Assert.Equal(AuthBlob.Parse(updated).GetIdentity().Fingerprint(), (await store.ListAsync(default)).Single().IdentityFingerprint);
    }

    [Fact]
    public async Task TryReplaceAuthAsync_does_not_overwrite_a_newer_login()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.Path, new PrefixProtector(), new AtomicFileStore());
        var original = TestAuth.OAuth("old", "old-refresh", "acct-1");
        var profile = await store.SaveAsync("Work", original, default);
        var newLogin = TestAuth.OAuth("new-login", "new-refresh", "acct-1");
        await store.ReplaceAuthAsync(profile.Id, newLogin, default);

        var replaced = await store.TryReplaceAuthAsync(profile.Id, original, TestAuth.OAuth("rotated", "rotated-refresh", "acct-1"), default);

        Assert.False(replaced);
        Assert.Equal(newLogin, await store.LoadAuthAsync(profile.Id, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaceAuth_rolls_back_credentials_if_updating_fingerprint_fails(bool conditional)
    {
        using var directory = new TestDirectory();
        var files = new ConfigurationFailingFileStore();
        var store = new AccountStore(directory.Path, new PrefixProtector(), files);
        var original = TestAuth.OAuth("old", "old-refresh", "acct-1");
        var profile = await store.SaveAsync("Work", original, default);
        var updated = TestAuth.OAuth("new", "new-refresh", "acct-1", TestAuth.IdToken("subject", "mail@example.com"));
        files.FailConfigurationWrites = true;

        if (conditional) await Assert.ThrowsAsync<IOException>(() => store.TryReplaceAuthAsync(profile.Id, original, updated, default));
        else await Assert.ThrowsAsync<IOException>(() => store.ReplaceAuthAsync(profile.Id, updated, default));

        Assert.Equal(original, await store.LoadAuthAsync(profile.Id, default));
        Assert.Equal(profile.IdentityFingerprint, (await store.ListAsync(default)).Single().IdentityFingerprint);
    }

    private sealed class ConfigurationFailingFileStore : IAtomicFileStore
    {
        private readonly AtomicFileStore _inner = new();
        public bool FailConfigurationWrites { get; set; }
        public bool Exists(string path) => _inner.Exists(path);
        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
        public void Delete(string path) => _inner.Delete(path);
        public void WriteAtomically(string path, byte[] contents)
        {
            if (FailConfigurationWrites && System.IO.Path.GetFileName(path) == "config.json") throw new IOException("Simulated configuration write failure");
            _inner.WriteAtomically(path, contents);
        }
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
