using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Tests.Auth;

public sealed class AccountImportServiceTests
{
    [Fact]
    public async Task ImportAsync_rejects_duplicate_identity_without_overwrite_confirmation()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.Path, new PassthroughProtector(), new AtomicFileStore());
        var service = new AccountImportService(store);
        await service.ImportAsync("First", TestAuth.OAuth("a1", "r1", "acct-1"), overwriteDuplicate: false, default);

        var error = await Assert.ThrowsAsync<DuplicateAccountException>(() =>
            service.ImportAsync("Second", TestAuth.OAuth("a2", "r2", "acct-1"), overwriteDuplicate: false, default));

        Assert.Equal("First", error.Existing.Label);
        Assert.Single(await store.ListAsync(default));
    }

    [Fact]
    public async Task ImportAsync_updates_duplicate_after_confirmation()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.Path, new PassthroughProtector(), new AtomicFileStore());
        var service = new AccountImportService(store);
        var first = await service.ImportAsync("First", TestAuth.OAuth("a1", "r1", "acct-1"), false, default);

        var updated = await service.ImportAsync("Updated", TestAuth.OAuth("a2", "r2", "acct-1"), true, default);

        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("Updated", updated.Label);
        Assert.Contains("a2", System.Text.Encoding.UTF8.GetString(await store.LoadAuthAsync(first.Id, default)));
    }

    [Fact]
    public async Task ImportAsync_recognizes_same_account_when_new_auth_adds_identity_claims()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.Path, new PassthroughProtector(), new AtomicFileStore());
        var service = new AccountImportService(store);
        var first = await service.ImportAsync("First", TestAuth.OAuth("a1", "r1", "acct-1"), false, default);

        var updated = await service.ImportAsync("Updated", TestAuth.OAuth("a2", "r2", "acct-1", TestAuth.IdToken("user-1", "mail@example.com")), true, default);

        Assert.Equal(first.Id, updated.Id);
        Assert.Single(await store.ListAsync(default));
    }

    [Fact]
    public async Task ReauthenticateAsync_replaces_only_the_expected_identity()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.Path, new PassthroughProtector(), new AtomicFileStore());
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("old", "old-r", "acct-1"), default);
        var service = new AccountImportService(store);

        var result = await service.ReauthenticateAsync(profile.Id, TestAuth.OAuth("new", "new-r", "acct-1"), default);

        Assert.Equal(profile.Id, result.Id);
        Assert.Contains("new", System.Text.Encoding.UTF8.GetString(await store.LoadAuthAsync(profile.Id, default)));
    }

    [Fact]
    public async Task ReauthenticateAsync_rejects_a_different_account()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.Path, new PassthroughProtector(), new AtomicFileStore());
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("old", "old-r", "acct-1"), default);
        var service = new AccountImportService(store);

        await Assert.ThrowsAsync<AuthBlobException>(() =>
            service.ReauthenticateAsync(profile.Id, TestAuth.OAuth("other", "other-r", "acct-2"), default));

        Assert.Contains("old", System.Text.Encoding.UTF8.GetString(await store.LoadAuthAsync(profile.Id, default)));
    }

    internal sealed class PassthroughProtector : ICredentialProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray();
    }
}
