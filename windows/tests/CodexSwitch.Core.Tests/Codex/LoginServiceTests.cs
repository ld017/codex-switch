using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Codex;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Tests.Auth;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class LoginServiceTests
{
    [Fact]
    public async Task Browser_login_runs_hidden_isolated_login_with_file_credentials_and_cleans_plaintext()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var runner = new LoginProcessRunner(TestAuth.OAuth("access", "refresh", "acct-1"));
        var service = new LoginService("codex.exe", directory.File("tmp"), runner, new AccountImportService(store));

        var profile = await service.LoginAsync("Work", LoginMode.Browser, overwriteDuplicate: false, default);

        Assert.Equal("Work", profile.Label);
        Assert.False(runner.WasVisible);
        Assert.Equal(["login"], runner.Arguments);
        Assert.Equal(runner.CodexHome, runner.WorkingDirectory);
        Assert.Contains("cli_auth_credentials_store = \"file\"", runner.ConfigText, StringComparison.Ordinal);
        Assert.False(Directory.Exists(runner.CodexHome));
    }

    [Fact]
    public async Task Device_code_login_runs_visible_with_device_auth_argument()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var runner = new LoginProcessRunner(TestAuth.OAuth("access", "refresh", "acct-1"));
        var service = new LoginService("codex.exe", directory.File("tmp"), runner, new AccountImportService(store));

        _ = await service.LoginAsync("Work", LoginMode.DeviceCode, overwriteDuplicate: false, default);

        Assert.True(runner.WasVisible);
        Assert.Equal(["login", "--device-auth"], runner.Arguments);
    }

    [Fact]
    public async Task LoginAsync_cleans_temporary_home_after_nonzero_exit()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var runner = new LoginProcessRunner(auth: null, exitCode: 1);
        var service = new LoginService("codex.exe", directory.File("tmp"), runner, new AccountImportService(store));

        await Assert.ThrowsAsync<LoginException>(() => service.LoginAsync("Work", false, default));

        Assert.False(Directory.Exists(runner.CodexHome));
    }

    [Fact]
    public async Task LoginAsync_redacts_cli_failure_output()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var runner = new LoginProcessRunner(auth: null, exitCode: 1, standardError: "{\"access_token\":\"secret-token\"} login failed");
        var service = new LoginService("codex.exe", directory.File("tmp"), runner, new AccountImportService(store));

        var error = await Assert.ThrowsAsync<LoginException>(() => service.LoginAsync("Work", false, default));

        Assert.DoesNotContain("secret-token", error.Message, StringComparison.Ordinal);
        Assert.Contains("<redacted>", error.Message, StringComparison.Ordinal);
        Assert.Contains("login failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginAsync_explains_control_c_exit_as_cancellation()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var runner = new LoginProcessRunner(auth: null, exitCode: unchecked((int)0xC000013A));
        var service = new LoginService("codex.exe", directory.File("tmp"), runner, new AccountImportService(store));

        var error = await Assert.ThrowsAsync<LoginException>(() => service.LoginAsync("Work", false, default));

        Assert.Contains("取消", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReauthenticateAsync_updates_selected_profile_without_creating_another_profile()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("old", "old-r", "acct-1"), default);
        var runner = new LoginProcessRunner(TestAuth.OAuth("new", "new-r", "acct-1"));
        var service = new LoginService("codex.exe", directory.File("tmp"), runner, new AccountImportService(store));

        var result = await service.ReauthenticateAsync(profile, LoginMode.Browser, default);

        Assert.Equal(profile.Id, result.Id);
        Assert.Single(await store.ListAsync(default));
    }

    [Fact]
    public async Task LoginAsync_reuses_same_login_result_when_duplicate_is_confirmed()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        await store.SaveAsync("Existing", TestAuth.OAuth("old", "old-r", "acct-1"), default);
        var runner = new LoginProcessRunner(TestAuth.OAuth("new", "new-r", "acct-1"));
        var service = new LoginService("codex.exe", directory.File("tmp"), runner, new AccountImportService(store));

        var profile = await service.LoginAsync("Updated", _ => true, default);

        Assert.Equal(1, runner.CallCount);
        Assert.Equal("Updated", profile.Label);
    }

    [Fact]
    public async Task LoginAsync_holds_lease_and_plaintext_until_import_completes_then_retains_failed_import_for_recovery()
    {
        using var directory = new TestDirectory();
        var runner = new LoginProcessRunner(TestAuth.OAuth("access", "refresh", "acct-1"));
        var protector = new ImportFailureProtector(() =>
        {
            Assert.True(File.Exists(Path.Combine(runner.CodexHome, "auth.json")));
            Assert.True(TemporaryHomeLease.IsActive(runner.CodexHome));
        });
        var store = new AccountStore(directory.File("data"), protector, new AtomicFileStore());
        var service = new LoginService("codex.exe", directory.File("tmp"), runner, new AccountImportService(store));

        await Assert.ThrowsAsync<IOException>(() => service.LoginAsync("Work", false, default));

        Assert.True(Directory.Exists(runner.CodexHome));
        Assert.True(TemporaryHomeLease.HasRecoveryRequired(runner.CodexHome));
        Assert.False(TemporaryHomeLease.IsActive(runner.CodexHome));
    }

    [Fact]
    public async Task LoginAsync_cleanup_failure_does_not_reverse_successful_import()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var runner = new LoginProcessRunner(TestAuth.OAuth("access", "refresh", "acct-1"));
        var service = new LoginService("codex.exe", directory.File("tmp"), runner, new AccountImportService(store), new ThrowingCleaner());

        var profile = await service.LoginAsync("Work", false, default);

        Assert.Equal("Work", profile.Label);
        Assert.True(TemporaryHomeLease.IsCleanupReady(runner.CodexHome));
        Assert.False(TemporaryHomeLease.IsActive(runner.CodexHome));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginAsync_retains_interrupted_atomic_credentials_after_cli_failure(bool throwsAfterWrite)
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var runner = new LoginProcessRunner(TestAuth.OAuth("access", "refresh", "acct-1"), exitCode: 1,
            writeAtomicOnly: true, throwAfterWrite: throwsAfterWrite);
        var service = new LoginService("codex.exe", directory.File("tmp"), runner, new AccountImportService(store));

        if (throwsAfterWrite) await Assert.ThrowsAsync<IOException>(() => service.LoginAsync("Work", false, default));
        else await Assert.ThrowsAsync<LoginException>(() => service.LoginAsync("Work", false, default));

        Assert.True(Directory.Exists(runner.CodexHome));
        Assert.Single(Directory.GetFiles(runner.CodexHome, ".auth.json.*.tmp"));
        Assert.True(TemporaryHomeLease.HasRecoveryRequired(runner.CodexHome));
        Assert.False(TemporaryHomeLease.IsCleanupReady(runner.CodexHome));
    }

    private sealed class ImportFailureProtector(Action inspect) : ICredentialProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) { inspect(); throw new IOException("store failure"); }
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray();
    }

    private sealed class ThrowingCleaner : CodexSwitch.Core.Usage.ITemporaryDirectoryCleaner
    {
        public Task<bool> TryDeleteAsync(string path, CancellationToken cancellationToken) => throw new IOException("cleanup failure");
        public Task CleanStaleAsync(string root, TimeSpan minimumAge, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class LoginProcessRunner(byte[]? auth, int exitCode = 0, string standardError = "", bool writeAtomicOnly = false, bool throwAfterWrite = false) : IProcessRunner
    {
        public bool WasVisible { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public string CodexHome { get; private set; } = string.Empty;
        public string? WorkingDirectory { get; private set; }
        public string ConfigText { get; private set; } = string.Empty;
        public int CallCount { get; private set; }

        public async Task<ProcessResult> RunAsync(ProcessSpec specification, CancellationToken cancellationToken)
        {
            CallCount++;
            WasVisible = specification.Visible;
            Arguments = specification.Arguments;
            CodexHome = specification.Environment!["CODEX_HOME"]!;
            WorkingDirectory = specification.WorkingDirectory;
            ConfigText = await File.ReadAllTextAsync(Path.Combine(CodexHome, "config.toml"), cancellationToken);
            if (auth is not null)
            {
                Directory.CreateDirectory(CodexHome);
                await File.WriteAllBytesAsync(Path.Combine(CodexHome, writeAtomicOnly ? $".auth.json.{Guid.NewGuid():N}.tmp" : "auth.json"), auth, cancellationToken);
            }

            if (throwAfterWrite) throw new IOException("synthetic interrupted atomic write");

            return new ProcessResult(exitCode, string.Empty, standardError);
        }
    }
}
