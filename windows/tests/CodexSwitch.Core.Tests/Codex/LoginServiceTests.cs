using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Codex;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Tests.Auth;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class LoginServiceTests
{
    [Fact]
    public async Task LoginAsync_runs_visible_isolated_login_imports_auth_and_cleans_plaintext()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var runner = new LoginProcessRunner(TestAuth.OAuth("access", "refresh", "acct-1"));
        var service = new LoginService("codex.exe", directory.File("tmp"), runner, new AccountImportService(store));

        var profile = await service.LoginAsync("Work", overwriteDuplicate: false, default);

        Assert.Equal("Work", profile.Label);
        Assert.True(runner.WasVisible);
        Assert.Equal(["login"], runner.Arguments);
        Assert.False(Directory.Exists(runner.CodexHome));
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

    private sealed class LoginProcessRunner(byte[]? auth, int exitCode = 0) : IProcessRunner
    {
        public bool WasVisible { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public string CodexHome { get; private set; } = string.Empty;
        public int CallCount { get; private set; }

        public async Task<ProcessResult> RunAsync(ProcessSpec specification, CancellationToken cancellationToken)
        {
            CallCount++;
            WasVisible = specification.Visible;
            Arguments = specification.Arguments;
            CodexHome = specification.Environment!["CODEX_HOME"]!;
            if (auth is not null)
            {
                Directory.CreateDirectory(CodexHome);
                await File.WriteAllBytesAsync(Path.Combine(CodexHome, "auth.json"), auth, cancellationToken);
            }

            return new ProcessResult(exitCode, string.Empty, string.Empty);
        }
    }
}
