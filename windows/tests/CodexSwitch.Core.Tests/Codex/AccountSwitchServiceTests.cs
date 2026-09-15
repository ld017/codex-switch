using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Codex;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Tests.Auth;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class AccountSwitchServiceTests
{
    [Fact]
    public async Task SwitchAsync_rejects_account_change_when_provider_is_sub2api()
    {
        using var directory = new TestDirectory();
        var configPath = directory.File("config.toml");
        await File.WriteAllTextAsync(configPath, "model = \"gpt-5\"\nmodel_provider = \"sub2api\"\n");
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("a", "r", "acct-1"), default);
        var liveAuthPath = directory.File("auth.json");
        var original = TestAuth.OAuth("live", "live-r", "acct-live");
        await File.WriteAllBytesAsync(liveAuthPath, original);
        var service = new AccountSwitchService(configPath, liveAuthPath, directory.File("auth-backups"), store, new AtomicFileStore());

        var error = await Assert.ThrowsAsync<AccountSwitchException>(() => service.SwitchAsync(profile.Id, default));

        Assert.Equal(AccountSwitchStage.Prerequisites, error.Stage);
        Assert.Equal(original, await File.ReadAllBytesAsync(liveAuthPath));
    }

    [Fact]
    public async Task SwitchAsync_replaces_live_auth_and_marks_profile_active()
    {
        using var directory = new TestDirectory();
        var configPath = directory.File("config.toml");
        await File.WriteAllTextAsync(configPath, "model = \"gpt-5\"\nmodel_provider = \"openai\"\n");
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var first = await store.SaveAsync("First", TestAuth.OAuth("a1", "r1", "acct-1"), default);
        var secondAuth = TestAuth.OAuth("a2", "r2", "acct-2");
        var second = await store.SaveAsync("Second", secondAuth, default);
        await store.SetActiveProfileIdAsync(first.Id, default);
        var liveAuthPath = directory.File("auth.json");
        await File.WriteAllBytesAsync(liveAuthPath, TestAuth.OAuth("live", "live-r", "acct-live"));
        var service = new AccountSwitchService(configPath, liveAuthPath, directory.File("auth-backups"), store, new AtomicFileStore());

        var result = await service.SwitchAsync(second.Id, default);

        Assert.Equal(first.Id, result.PreviousProfileId);
        Assert.Equal(second.Id, result.CurrentProfileId);
        Assert.Equal(secondAuth, await File.ReadAllBytesAsync(liveAuthPath));
        Assert.Equal(second.Id, await store.GetActiveProfileIdAsync(default));
        Assert.NotNull(result.LiveAuthBackupPath);
        Assert.True(File.Exists(result.LiveAuthBackupPath));
    }
}
