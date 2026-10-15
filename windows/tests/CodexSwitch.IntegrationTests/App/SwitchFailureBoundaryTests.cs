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
        public Setup(ICodexLifecycle? lifecycle = null)
        {
            Paths = new AppPaths(new Dictionary<string, string?> { ["CODEX_HOME"] = Path.Combine(_root, "codex"), ["CODEX_SWITCH_DATA_HOME"] = Path.Combine(_root, "data") });
            Directory.CreateDirectory(Paths.CodexHome);
            File.WriteAllText(Paths.ConfigPath, "model_provider = \"openai\"\n");
            File.WriteAllBytes(Paths.AuthPath, Auth("original"));
            var files = new AtomicFileStore();
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
