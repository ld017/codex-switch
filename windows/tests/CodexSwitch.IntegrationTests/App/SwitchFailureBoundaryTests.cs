using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CodexSwitch.App;
using CodexSwitch.App.Windows;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Codex;
using CodexSwitch.Core.Files;

namespace CodexSwitch.IntegrationTests.App;

public sealed class SwitchFailureBoundaryTests
{
    [Fact]
    public void Fault_after_an_await_is_observed_by_the_ui_event_boundary()
        => OnSta(async () =>
        {
            using var setup = new Setup();
            var reports = 0;
            using var form = new MainForm(setup.Services, false, (_, _) => reports++);
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();
            Func<Task> failed = async () => { await Task.Yield(); throw new IOException("Synthetic cancelled-refresh cache failure"); };

            await (Task)Method("RunUiEventAsync").Invoke(form, ["后台刷新", failed, true])!;

            Assert.Equal(1, reports);
            Assert.False(form.IsDisposed);
            Assert.Contains("cancelled-refresh cache failure", File.ReadAllText(Path.Combine(setup.Paths.LogDirectory, "codex-switch-crash.log")));
        });

    [Fact]
    public void Failing_error_presenter_cannot_fault_the_switch_task()
        => OnSta(async () =>
        {
            using var setup = new Setup();
            using var form = new MainForm(setup.Services, false, (_, _) => throw new InvalidOperationException("Synthetic presenter failure"));
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();

            await Operation(form, () => Task.FromException(new IOException("Synthetic switch fault")));

            Assert.False(form.IsDisposed);
            Assert.False(Field<bool>(form, "_busy"));
            Assert.Contains("Synthetic presenter failure", File.ReadAllText(Path.Combine(setup.Paths.LogDirectory, "codex-switch-crash.log")));
        });

    [Fact]
    public void Failure_immediately_after_confirmation_keeps_window_and_releases_operation_lock()
        => OnSta(async () =>
        {
            using var setup = new Setup();
            var reports = new List<Exception>();
            using var form = new MainForm(setup.Services, false, (_, error) => reports.Add(error));
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();
            _ = form.Handle;
            var buttons = Field<List<Button>>(form, "_actionButtons");
            var failed = new InvalidOperationException("Synthetic immediate failure {\"access_token\":\"must-not-leak\"}");

            await Operation(form, () => Task.FromException(failed));

            Assert.Same(failed, Assert.Single(reports));
            Assert.False(form.IsDisposed);
            Assert.False(Field<bool>(form, "_busy"));
            Assert.All(buttons, button => Assert.True(button.Enabled));
            var log = File.ReadAllText(Path.Combine(setup.Paths.LogDirectory, "codex-switch.log"));
            Assert.Contains("确认通过", log);
            Assert.Contains("Synthetic immediate failure", log);
            Assert.DoesNotContain("must-not-leak", log);
        });

    [Fact]
    public void Process_snapshot_failure_does_not_change_login_or_close_switcher()
        => OnSta(async () =>
        {
            var activationCount = 0;
            var lifecycle = new WindowsCodexLifecycle(
                () => throw new Win32Exception(5, "Synthetic snapshot access failure"),
                () => activationCount++);
            using var setup = new Setup(lifecycle);
            var profile = await setup.Accounts.SaveAsync("Target", Auth("target"), default);
            var before = await File.ReadAllBytesAsync(setup.Paths.AuthPath);
            var reports = new List<Exception>();
            using var form = new MainForm(setup.Services, false, (_, error) => reports.Add(error));
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();
            _ = form.Handle;

            await Operation(form, () => (Task)Method("ApplyAccountAsync").Invoke(form, [profile.Id, false])!);

            Assert.Single(reports);
            Assert.False(form.IsDisposed);
            Assert.False(Field<bool>(form, "_busy"));
            Assert.Equal(before, await File.ReadAllBytesAsync(setup.Paths.AuthPath));
            Assert.Equal(0, activationCount);
            var log = File.ReadAllText(Path.Combine(setup.Paths.LogDirectory, "codex-switch.log"));
            Assert.Contains("请求 Codex 正常退出", log);
            Assert.DoesNotContain("开始写入账号", log);
        });

    [Fact]
    public void Invalid_target_does_not_close_Codex_or_change_auth()
        => OnSta(async () =>
        {
            var lifecycle = new SyntheticLifecycle();
            using var setup = new Setup(lifecycle);
            var before = await File.ReadAllBytesAsync(setup.Paths.AuthPath);
            using var form = new MainForm(setup.Services, false, (_, _) => { });
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();

            await Assert.ThrowsAsync<AccountSwitchException>(() => ApplyAccount(form, Guid.NewGuid()));

            Assert.Empty(lifecycle.Calls);
            Assert.Equal(before, await File.ReadAllBytesAsync(setup.Paths.AuthPath));
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Confirmed_switch_closes_then_writes_auth_then_launches(bool normalExit)
        => OnSta(async () =>
        {
            var calls = new List<string>();
            var lifecycle = new SyntheticLifecycle(calls) { NormalExit = normalExit, ForceExit = true };
            var files = new ObservedFileStore();
            using var setup = new Setup(lifecycle, files);
            var targetAuth = Auth("target");
            var target = await setup.Accounts.SaveAsync("Target", targetAuth, default);
            var before = await File.ReadAllBytesAsync(setup.Paths.AuthPath);
            files.AfterWrite = path => { if (path == setup.Paths.AuthPath) calls.Add("write-auth"); };
            lifecycle.WhenClose = () => Assert.Equal(before, File.ReadAllBytes(setup.Paths.AuthPath));
            lifecycle.WhenForce = () => Assert.Equal(before, File.ReadAllBytes(setup.Paths.AuthPath));
            lifecycle.WhenLaunch = () => Assert.Equal(targetAuth, File.ReadAllBytes(setup.Paths.AuthPath));
            using var form = new MainForm(setup.Services, false, (_, _) => { });
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();

            await ApplyAccount(form, target.Id);

            Assert.Equal(normalExit ? ["close", "write-auth", "launch"] : ["close", "force", "write-auth", "launch"], calls);
            Assert.Equal(TimeSpan.FromSeconds(3), lifecycle.RequestedTimeout);
            Assert.Equal(targetAuth, await File.ReadAllBytesAsync(setup.Paths.AuthPath));
            Assert.Equal(target.Id, await setup.Accounts.GetActiveProfileIdAsync(default));
            Assert.False(form.IsDisposed);
        });

    [Fact]
    public void Failed_force_close_does_not_write_auth_or_launch()
        => OnSta(async () =>
        {
            var lifecycle = new SyntheticLifecycle { NormalExit = false, ForceExit = false };
            using var setup = new Setup(lifecycle);
            var target = await setup.Accounts.SaveAsync("Target", Auth("target"), default);
            var before = await File.ReadAllBytesAsync(setup.Paths.AuthPath);
            using var form = new MainForm(setup.Services, false, (_, _) => { });
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAccount(form, target.Id));

            Assert.Contains("本次未修改账号凭据", error.Message);
            Assert.Equal(["close", "force"], lifecycle.Calls);
            Assert.Equal(before, await File.ReadAllBytesAsync(setup.Paths.AuthPath));
            Assert.False(Directory.Exists(setup.Paths.AccountBackupDirectory));
            Assert.False(form.IsDisposed);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shutdown_exception_does_not_write_auth_or_launch(bool duringForce)
        => OnSta(async () =>
        {
            var failure = new IOException("Synthetic shutdown failure");
            var lifecycle = new SyntheticLifecycle
            {
                NormalExit = false,
                CloseError = duringForce ? null : failure,
                ForceError = duringForce ? failure : null,
            };
            using var setup = new Setup(lifecycle);
            var target = await setup.Accounts.SaveAsync("Target", Auth("target"), default);
            var before = await File.ReadAllBytesAsync(setup.Paths.AuthPath);
            using var form = new MainForm(setup.Services, false, (_, _) => { });
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();

            var error = await Assert.ThrowsAsync<IOException>(() => ApplyAccount(form, target.Id));

            Assert.Same(failure, error);
            Assert.Equal(duringForce ? ["close", "force"] : ["close"], lifecycle.Calls);
            Assert.Equal(before, await File.ReadAllBytesAsync(setup.Paths.AuthPath));
            Assert.False(Directory.Exists(setup.Paths.AccountBackupDirectory));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancelled_shutdown_does_not_write_auth_or_launch(bool duringForce)
        => OnSta(async () =>
        {
            var lifecycle = new SyntheticLifecycle { NormalExit = false, ForceExit = true };
            using var setup = new Setup(lifecycle);
            var target = await setup.Accounts.SaveAsync("Target", Auth("target"), default);
            var before = await File.ReadAllBytesAsync(setup.Paths.AuthPath);
            using var form = new MainForm(setup.Services, false, (_, _) => { });
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();
            var lifetime = Field<CancellationTokenSource>(form, "_lifetime");
            if (duringForce) lifecycle.WhenForce = lifetime.Cancel;
            else lifecycle.WhenClose = lifetime.Cancel;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApplyAccount(form, target.Id));

            Assert.Equal(duringForce ? ["close", "force"] : ["close"], lifecycle.Calls);
            Assert.Equal(before, await File.ReadAllBytesAsync(setup.Paths.AuthPath));
            Assert.False(Directory.Exists(setup.Paths.AccountBackupDirectory));
        });

    [Fact]
    public void A_failed_log_write_cannot_escape_the_ui_error_boundary()
        => OnSta(async () =>
        {
            using var setup = new Setup();
            Directory.CreateDirectory(setup.Paths.DataHome);
            await File.WriteAllTextAsync(setup.Paths.LogDirectory, "Synthetic file blocks log directory");
            var reports = 0;
            using var form = new MainForm(setup.Services, false, (_, _) => reports++);
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();

            await Operation(form, () => Task.FromException(new IOException("Synthetic write failure")));

            Assert.Equal(1, reports);
            Assert.False(form.IsDisposed);
            Assert.False(Field<bool>(form, "_busy"));
        });

    private static Task Operation(MainForm form, Func<Task> action)
        => (Task)Method("RunUiOperationAsync").Invoke(form, ["切换账号", action])!;
    private static Task ApplyAccount(MainForm form, Guid profileId)
        => (Task)Method("ApplyAccountAsync").Invoke(form, [profileId, false])!;
    private static MethodInfo Method(string name) => typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static byte[] Auth(string account) => System.Text.Encoding.UTF8.GetBytes(
        "{\"tokens\":{\"access_token\":\"synthetic-a\",\"refresh_token\":\"synthetic-r\",\"account_id\":\"" + account + "\"}}");

    private sealed class Setup : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "switch-boundary-tests", Guid.NewGuid().ToString("N"));
        public AppPaths Paths { get; }
        public AccountStore Accounts { get; }
        public AppServices Services { get; }
        public Setup(ICodexLifecycle? lifecycle = null, IAtomicFileStore? files = null)
        {
            Paths = new AppPaths(new Dictionary<string, string?> { ["CODEX_HOME"] = Path.Combine(_root, "codex"), ["CODEX_SWITCH_DATA_HOME"] = Path.Combine(_root, "data") });
            Directory.CreateDirectory(Paths.CodexHome);
            File.WriteAllText(Paths.ConfigPath, "model_provider = \"openai\"\n");
            File.WriteAllBytes(Paths.AuthPath, Auth("original"));
            files ??= new AtomicFileStore();
            Accounts = new AccountStore(Paths.DataHome, new Protector(), files);
            var accountSwitch = new AccountSwitchService(Paths.ConfigPath, Paths.AuthPath, Paths.AccountBackupDirectory, Accounts, files);
            Services = new AppServices(Paths, Accounts, null!, accountSwitch, null!, null!, lifecycle ?? new UnusedLifecycle(), null!, null!);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    private sealed class Protector : ICredentialProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> value) => value.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> value) => value.ToArray();
    }
    private sealed class UnusedLifecycle : ICodexLifecycle
    {
        public Task<CodexShutdownResult> RequestCloseAsync(TimeSpan timeout, CancellationToken token) => throw new InvalidOperationException("Lifecycle must not run in this test");
        public Task<bool> ForceCloseCapturedAsync(CancellationToken token) => throw new InvalidOperationException("Lifecycle must not run in this test");
        public Task LaunchAsync(CancellationToken token) => throw new InvalidOperationException("Lifecycle must not run in this test");
    }
    private sealed class SyntheticLifecycle(List<string>? calls = null) : ICodexLifecycle
    {
        public bool NormalExit { get; init; }
        public bool ForceExit { get; init; }
        public Exception? CloseError { get; init; }
        public Exception? ForceError { get; init; }
        public Action? WhenClose { get; set; }
        public Action? WhenForce { get; set; }
        public Action? WhenLaunch { get; set; }
        public List<string> Calls { get; } = calls ?? [];
        public TimeSpan RequestedTimeout { get; private set; }
        public Task<CodexShutdownResult> RequestCloseAsync(TimeSpan timeout, CancellationToken token)
        {
            Calls.Add("close");
            RequestedTimeout = timeout;
            if (CloseError is not null) return Task.FromException<CodexShutdownResult>(CloseError);
            WhenClose?.Invoke();
            return Task.FromResult(new CodexShutdownResult(NormalExit, [42], "Synthetic task still active"));
        }
        public Task<bool> ForceCloseCapturedAsync(CancellationToken token)
        {
            Calls.Add("force");
            if (ForceError is not null) return Task.FromException<bool>(ForceError);
            WhenForce?.Invoke();
            return Task.FromResult(ForceExit);
        }
        public Task LaunchAsync(CancellationToken token)
        {
            Calls.Add("launch");
            WhenLaunch?.Invoke();
            return Task.CompletedTask;
        }
    }
    private sealed class ObservedFileStore : IAtomicFileStore
    {
        private readonly AtomicFileStore _inner = new();
        public Action<string>? AfterWrite { get; set; }
        public bool Exists(string path) => _inner.Exists(path);
        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
        public void Delete(string path) => _inner.Delete(path);
        public void WriteAtomically(string path, byte[] contents)
        {
            _inner.WriteAtomically(path, contents);
            AfterWrite?.Invoke(path);
        }
    }
    private static void OnSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var context = new ApplicationContext();
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); }
                catch (Exception error) { failure = error; }
                finally { context.ExitThread(); }
            });
            Application.Run(context);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Synthetic switch test did not finish");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
