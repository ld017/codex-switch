using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Codex;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Tests.Auth;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class AccountSwitchServiceTests
{
    [Fact]
    public async Task ValidateAsync_rejects_swapped_account_file_without_modifying_live_auth()
    {
        using var setup = new SwitchSetup();
        var target = await setup.Accounts.SaveAsync("Target", TestAuth.OAuth("a", "r", "acct-target"), default);
        await File.WriteAllBytesAsync(setup.Directory.File($"data/accounts/{target.Id:N}.bin"), TestAuth.OAuth("other", "other-r", "acct-other"));

        var error = await Assert.ThrowsAsync<AccountSwitchException>(() => setup.Service.ValidateAsync(target.Id, default));

        Assert.Equal(AccountSwitchStage.Validation, error.Stage);
        Assert.Equal(setup.Original, await File.ReadAllBytesAsync(setup.LivePath));
        Assert.False(Directory.Exists(setup.BackupPath));
    }

    [Fact]
    public async Task SwitchAsync_preserves_rotated_live_token_for_outgoing_account()
    {
        using var setup = new SwitchSetup();
        var first = await setup.Accounts.SaveAsync("First", TestAuth.OAuth("old", "old-r", "acct-live"), default);
        var targetAuth = TestAuth.OAuth("target", "target-r", "acct-target");
        var target = await setup.Accounts.SaveAsync("Target", targetAuth, default);

        var result = await setup.Service.SwitchAsync(target.Id, default);

        Assert.Equal(setup.Original, await setup.Accounts.LoadAuthAsync(first.Id, default));
        Assert.Equal(setup.Original, await File.ReadAllBytesAsync(result.LiveAuthBackupPath!));
        Assert.Equal(targetAuth, await File.ReadAllBytesAsync(setup.LivePath));
        Assert.True(File.Exists(result.LiveAuthBackupPath + ".cleanup-ready"));
    }

    [Fact]
    public async Task SwitchAsync_to_current_account_retains_new_live_token()
    {
        using var setup = new SwitchSetup();
        var current = await setup.Accounts.SaveAsync("Current", TestAuth.OAuth("old", "old-r", "acct-live"), default);

        await setup.Service.SwitchAsync(current.Id, default);

        Assert.Equal(setup.Original, await File.ReadAllBytesAsync(setup.LivePath));
        Assert.Equal(setup.Original, await setup.Accounts.LoadAuthAsync(current.Id, default));
    }

    [Fact]
    public async Task SwitchAsync_after_explicit_reauthentication_uses_new_login_instead_of_old_live_token()
    {
        using var setup = new SwitchSetup();
        var freshLogin = TestAuth.OAuth("new-login", "new-login-r", "acct-live");
        var current = await setup.Accounts.SaveAsync("Current", freshLogin, default);

        var result = await setup.Service.SwitchAsync(current.Id, default, preserveTargetCredentials: true);

        Assert.Equal(freshLogin, await File.ReadAllBytesAsync(setup.LivePath));
        Assert.Equal(freshLogin, await setup.Accounts.LoadAuthAsync(current.Id, default));
        Assert.Equal(setup.Original, await File.ReadAllBytesAsync(result.LiveAuthBackupPath!));
    }

    [Fact]
    public async Task SwitchAsync_backup_failure_leaves_live_auth_and_metadata_unchanged()
    {
        using var setup = new SwitchSetup();
        var first = await setup.Accounts.SaveAsync("First", TestAuth.OAuth("a", "r", "acct-first"), default);
        var target = await setup.Accounts.SaveAsync("Target", TestAuth.OAuth("b", "s", "acct-target"), default);
        setup.Files.BeforeWrite = (path, _) => { if (path.StartsWith(setup.BackupPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Synthetic backup failure"); };

        var error = await Assert.ThrowsAsync<AccountSwitchException>(() => setup.Service.SwitchAsync(target.Id, default));

        Assert.Equal(AccountSwitchStage.Backup, error.Stage);
        Assert.Equal(setup.Original, await File.ReadAllBytesAsync(setup.LivePath));
        Assert.Equal(first.Id, await setup.Accounts.GetActiveProfileIdAsync(default));
    }

    [Fact]
    public async Task SwitchAsync_marker_failure_does_not_reverse_verified_success()
    {
        using var setup = new SwitchSetup();
        var target = await setup.Accounts.SaveAsync("Target", TestAuth.OAuth("b", "s", "acct-target"), default);
        setup.Files.BeforeWrite = (path, _) => { if (path.EndsWith(".cleanup-ready", StringComparison.Ordinal)) throw new IOException("marker failure"); };

        var result = await setup.Service.SwitchAsync(target.Id, default);

        Assert.Equal(target.Id, await setup.Accounts.GetActiveProfileIdAsync(default));
        Assert.True(File.Exists(result.LiveAuthBackupPath));
        Assert.False(File.Exists(result.LiveAuthBackupPath + ".cleanup-ready"));
    }

    [Fact]
    public async Task SwitchAsync_cancellation_after_live_write_still_restores_auth_and_metadata()
    {
        using var setup = new SwitchSetup();
        using var cancellation = new CancellationTokenSource();
        var first = await setup.Accounts.SaveAsync("First", TestAuth.OAuth("a", "r", "acct-first"), default);
        var targetAuth = TestAuth.OAuth("b", "s", "acct-target");
        var target = await setup.Accounts.SaveAsync("Target", targetAuth, default);
        setup.Files.AfterWrite = (path, bytes) => { if (path == setup.LivePath && bytes.SequenceEqual(targetAuth)) cancellation.Cancel(); };

        var error = await Assert.ThrowsAsync<AccountSwitchException>(() => setup.Service.SwitchAsync(target.Id, cancellation.Token));

        Assert.NotEqual(AccountSwitchStage.Recovery, error.Stage);
        Assert.Equal(setup.Original, await File.ReadAllBytesAsync(setup.LivePath));
        Assert.Equal(first.Id, await setup.Accounts.GetActiveProfileIdAsync(default));
    }

    [Fact]
    public async Task SwitchAsync_detects_same_identity_but_incorrect_written_token_and_restores()
    {
        using var setup = new SwitchSetup();
        var targetAuth = TestAuth.OAuth("b", "s", "acct-target");
        var target = await setup.Accounts.SaveAsync("Target", targetAuth, default);
        setup.Files.AfterWrite = (path, bytes) =>
        {
            if (path == setup.LivePath && bytes.SequenceEqual(targetAuth))
                File.WriteAllBytes(path, TestAuth.OAuth("wrong-access", "wrong-refresh", "acct-target"));
        };

        var error = await Assert.ThrowsAsync<AccountSwitchException>(() => setup.Service.SwitchAsync(target.Id, default));

        Assert.Equal(AccountSwitchStage.Validation, error.Stage);
        Assert.Equal(setup.Original, await File.ReadAllBytesAsync(setup.LivePath));
        Assert.Empty(Directory.GetFiles(setup.BackupPath, "*.cleanup-ready"));
    }

    [Fact]
    public async Task SwitchAsync_does_not_overwrite_credentials_changed_by_another_writer_before_commit()
    {
        using var setup = new SwitchSetup();
        var first = await setup.Accounts.SaveAsync("First", TestAuth.OAuth("old", "old-r", "acct-live"), default);
        var target = await setup.Accounts.SaveAsync("Target", TestAuth.OAuth("target", "target-r", "acct-target"), default);
        var concurrent = TestAuth.OAuth("concurrent", "concurrent-r", "acct-live");
        setup.Files.AfterWrite = (path, _) =>
        {
            if (path.EndsWith($"{first.Id:N}.bin", StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(setup.LivePath, concurrent);
        };

        var error = await Assert.ThrowsAsync<AccountSwitchException>(() => setup.Service.SwitchAsync(target.Id, default));

        Assert.Equal(AccountSwitchStage.Prerequisites, error.Stage);
        Assert.Equal(concurrent, await File.ReadAllBytesAsync(setup.LivePath));
        Assert.Equal(first.Id, await setup.Accounts.GetActiveProfileIdAsync(default));
    }

    private sealed class SwitchSetup : IDisposable
    {
        public TestDirectory Directory { get; } = new();
        public InterceptingFileStore Files { get; } = new();
        public AccountStore Accounts { get; }
        public AccountSwitchService Service { get; }
        public byte[] Original { get; } = TestAuth.OAuth("live-new", "live-new-r", "acct-live");
        public string LivePath => Directory.File("auth.json");
        public string BackupPath => Directory.File("auth-backups");

        public SwitchSetup()
        {
            var configPath = Directory.File("config.toml");
            File.WriteAllText(configPath, "model_provider = \"openai\"\n");
            File.WriteAllBytes(LivePath, Original);
            Accounts = new AccountStore(Directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), Files);
            Service = new AccountSwitchService(configPath, LivePath, BackupPath, Accounts, Files);
        }

        public void Dispose() => Directory.Dispose();
    }

    private sealed class InterceptingFileStore : IAtomicFileStore
    {
        private readonly AtomicFileStore _inner = new();
        public Action<string, byte[]>? BeforeWrite { get; set; }
        public Action<string, byte[]>? AfterWrite { get; set; }
        public bool Exists(string path) => _inner.Exists(path);
        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
        public void Delete(string path) => _inner.Delete(path);
        public void WriteAtomically(string path, byte[] contents)
        {
            BeforeWrite?.Invoke(path, contents);
            _inner.WriteAtomically(path, contents);
            AfterWrite?.Invoke(path, contents);
        }
    }

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
