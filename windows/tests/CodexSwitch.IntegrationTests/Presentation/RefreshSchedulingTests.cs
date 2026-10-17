using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Forms;
using CodexSwitch.App;
using CodexSwitch.App.Accounts;
using CodexSwitch.App.Windows;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Codex;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Presentation;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.IntegrationTests.Presentation;

public sealed class RefreshSchedulingTests
{
    [Fact]
    public void Repeated_manual_requests_during_auto_refresh_run_one_follow_up_round()
        => OnSta(async () =>
        {
            var entered = Signal();
            var release = Signal();
            var calls = 0;
            using var setup = await Setup.CreateAsync(async (_, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                return Response();
            });
            using var form = setup.CreateForm();
            var refresh = Refresh(form, false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var manual = Enumerable.Range(0, 20).Select(_ => Refresh(form, true)).ToArray();
            release.TrySetResult();
            await Task.WhenAll(manual.Append(refresh)).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, calls);
            Assert.False(Field<bool>(form, "_refreshing"));
            Assert.Empty(setup.Errors);
        });

    [Fact]
    public void Automatic_requests_do_not_create_another_round()
        => OnSta(async () =>
        {
            var entered = Signal();
            var release = Signal();
            var calls = 0;
            using var setup = await Setup.CreateAsync(async (_, token) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return Response();
            });
            using var form = setup.CreateForm();
            var refresh = Refresh(form, false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 20; index++) _ = Refresh(form, false);
            release.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, calls);
            Assert.Empty(setup.Errors);
        });

    [Fact]
    public void Auto_refresh_skips_recent_tokens_and_a_queued_manual_round_forces_them()
        => OnSta(async () =>
        {
            var entered = Signal();
            var release = Signal();
            var includeTokens = new List<bool>();
            using var setup = await Setup.CreateAsync(async (_, options, _, token) =>
            {
                includeTokens.Add(options.IncludeTokens);
                if (includeTokens.Count == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                return Response();
            });
            var profile = Assert.Single(await setup.Services.Accounts.ListAsync(default));
            var oldTokenDate = DateTimeOffset.UtcNow.AddSeconds(-20);
            var tokens = new TokenUsageSnapshot(new TokenUsageSummary(100), [], oldTokenDate);
            var snapshot = UsageSnapshotMapper.Map(Response(), oldTokenDate) with { TokenUsage = TokenUsageState.Available(tokens) };
            await new UsageCache(setup.Services.Paths.UsageCachePath, new AtomicFileStore())
                .SaveAsync(new Dictionary<Guid, UsageState> { [profile.Id] = UsageState.Available(snapshot) }, default);
            using var form = setup.CreateForm();

            var refresh = Refresh(form, false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _ = Refresh(form, true);
            release.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new[] { false, true }, includeTokens);
            Assert.Contains("可用 Token 统计", Field<ToolStripStatusLabel>(form, "_statusText").Text);
            Assert.Contains("独立读取时间", Field<ToolStripStatusLabel>(form, "_statusText").Text);
            Assert.Empty(setup.Errors);
        });

    [Fact]
    public void Mutation_discards_pending_requests_and_waits_for_cancelled_refresh_cleanup()
        => OnSta(async () =>
        {
            var entered = Signal();
            var cancelled = Signal();
            var releaseCleanup = Signal();
            var calls = 0;
            using var setup = await Setup.CreateAsync(async (_, token) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    await releaseCleanup.Task;
                    throw;
                }
                return Response();
            });
            using var form = setup.CreateForm();
            var refresh = Refresh(form, false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _ = Refresh(form, true);
            var mutated = false;
            var operation = Operation(form, () => { mutated = true; return Task.CompletedTask; });
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(mutated);
            Assert.False(Field<RefreshRequestScheduler>(form, "_refreshRequests").HasPendingForcedRequest);
            releaseCleanup.TrySetResult();
            await Task.WhenAll(refresh, operation).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(mutated);
            Assert.Equal(1, calls);
            Assert.False(Field<bool>(form, "_busy"));
            Assert.Empty(setup.Errors);
        });

    [Fact]
    public void Successful_operation_reloads_local_state_and_releases_busy_before_background_refresh_finishes()
        => OnSta(async () =>
        {
            var entered = Signal();
            var release = Signal();
            using var setup = await Setup.CreateAsync(async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return Response();
            });
            using var form = setup.CreateForm();
            var profile = Assert.Single(await setup.Services.Accounts.ListAsync(default));

            var operation = (Task)Method("RunUiOperationAndRefreshAsync").Invoke(form,
                ["Synthetic mutation", (Func<Task>)(() => setup.Services.Accounts.RenameAsync(profile.Id, "Reloaded locally", default))])!;
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(Field<bool>(form, "_busy"));
            Assert.Equal("Reloaded locally", Assert.Single(Field<IReadOnlyList<AccountProfile>>(form, "_accounts")).Label);
            var refresh = Field<Task>(form, "_refreshTask");
            Assert.False(refresh.IsCompleted);
            release.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(setup.Errors);
        });

    [Fact]
    public void Refresh_and_mutation_wait_for_existing_maintenance_before_accessing_data()
        => OnSta(async () =>
        {
            var calls = 0;
            using var setup = await Setup.CreateAsync((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Response());
            });
            using var form = setup.CreateForm();
            var maintenance = Signal();
            SetField(form, "_maintenanceTask", maintenance.Task);
            var refresh = Refresh(form, false);
            Assert.Equal(0, calls);
            var mutated = false;
            var operation = Operation(form, () => { mutated = true; return Task.CompletedTask; });
            Assert.False(mutated);
            maintenance.TrySetResult();
            await Task.WhenAll(refresh, operation).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(mutated);
            Assert.Equal(0, calls);
            Assert.Empty(setup.Errors);
        });

    [Fact]
    public void Closing_discards_pending_refreshes_and_does_not_wait_for_maintenance()
        => OnSta(async () =>
        {
            var calls = 0;
            using var setup = await Setup.CreateAsync((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Response());
            });
            using var form = setup.CreateForm();
            var maintenance = Signal();
            SetField(form, "_maintenanceTask", maintenance.Task);
            var refresh = Refresh(form, false);
            _ = Refresh(form, true);
            var args = new FormClosingEventArgs(CloseReason.UserClosing, false);
            typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [args]);
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(args.Cancel);
            Assert.False(maintenance.Task.IsCompleted);
            Assert.False(Field<RefreshRequestScheduler>(form, "_refreshRequests").HasPendingForcedRequest);
            Assert.Equal(0, calls);
            maintenance.TrySetResult();
            Assert.Empty(setup.Errors);
        });

    [Fact]
    public void Idle_maintenance_runs_once_and_uses_the_six_hour_interval()
        => OnSta(async () =>
        {
            using var setup = await Setup.CreateAsync((_, _) => Task.FromResult(Response()));
            setup.EnableMaintenance();
            using var form = setup.CreateForm();
            Method("SetBusy").Invoke(form, [true, null]);
            Method("TryStartMaintenance").Invoke(form, null);
            Assert.Null(Field<DateTimeOffset?>(form, "_lastMaintenanceAt"));
            Method("SetBusy").Invoke(form, [false, null]);
            Method("TryStartMaintenance").Invoke(form, null);
            var first = Field<Task>(form, "_maintenanceTask");
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Method("TryStartMaintenance").Invoke(form, null);
            Assert.Same(first, Field<Task>(form, "_maintenanceTask"));

            SetField(form, "_lastMaintenanceAt", DateTimeOffset.UtcNow.AddHours(-6).AddSeconds(-1));
            Method("TryStartMaintenance").Invoke(form, null);
            var next = Field<Task>(form, "_maintenanceTask");
            await next.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Field<DateTimeOffset?>(form, "_lastMaintenanceAt") > DateTimeOffset.UtcNow.AddMinutes(-1));
            Assert.Equal(2, File.ReadLines(Path.Combine(setup.Services.Paths.LogDirectory, "codex-switch.log"))
                .Count(line => line.Contains("数据维护:", StringComparison.Ordinal)));
            Assert.Empty(setup.Errors);
        });

    [Fact]
    public void Token_details_keep_the_latest_366_valid_dates_and_explain_the_limit()
        => OnSta(() =>
        {
            var now = DateTimeOffset.Now;
            var today = DateOnly.FromDateTime(now.LocalDateTime);
            var profile = new AccountProfile(Guid.NewGuid(), "Synthetic account", "synthetic");
            var days = Enumerable.Range(0, 500).Select(offset => new TokenUsageDailyBucket(today.AddDays(-offset).ToString("yyyy-MM-dd"), offset)).ToList();
            days.Add(new TokenUsageDailyBucket(today.ToString("yyyy-MM-dd"), 999));
            days.Add(new TokenUsageDailyBucket("invalid", 100));
            days.Add(new TokenUsageDailyBucket(today.AddDays(1).ToString("yyyy-MM-dd"), 100));
            var tokens = new TokenUsageSnapshot(new TokenUsageSummary(123456789), days, now);
            var snapshot = UsageSnapshotMapper.Map(Response(), now) with { TokenUsage = TokenUsageState.Available(tokens) };
            using var form = new TokenUsageDetailsForm([profile], new Dictionary<Guid, UsageState> { [profile.Id] = UsageState.Available(snapshot) }, profile.Id);
            var grid = Field<DataGridView>(form, "_days");

            Assert.Equal(366, grid.Rows.Count);
            Assert.Equal(today.ToString("yyyy-MM-dd"), grid.Rows[0].Cells[0].Value);
            Assert.Equal("999", grid.Rows[0].Cells[2].Value);
            Assert.Equal(today.AddDays(-365).ToString("yyyy-MM-dd"), grid.Rows[365].Cells[0].Value);
            Assert.Contains("123,456,789", Field<Label>(form, "_totals").Text);
            Assert.Contains(Descendants(form).OfType<Label>(), label => label.Text.Contains("366", StringComparison.Ordinal));
            return Task.CompletedTask;
        });

    private static IEnumerable<Control> Descendants(Control parent)
        => parent.Controls.Cast<Control>().SelectMany(child => Descendants(child).Prepend(child));

    private static Task Refresh(MainForm form, bool force)
        => (Task)Method("RefreshUsageAsync").Invoke(form, [force])!;
    private static Task Operation(MainForm form, Func<Task> action)
        => (Task)Method("RunUiOperationAsync").Invoke(form, ["Synthetic mutation", action])!;
    private static MethodInfo Method(string name) => typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void SetField(object instance, string name, object value)
        => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static RateLimitsResponse Response()
        => new(new RateLimitsSnapshot(new RateLimitWindow(12, 300, null), null, null, "pro"), null);

    private sealed class Setup : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "refresh-scheduling-tests", Guid.NewGuid().ToString("N"));
        public AppServices Services { get; private set; } = null!;
        public List<Exception> Errors { get; } = [];

        public static Task<Setup> CreateAsync(RateLimitsFetcher fetch)
            => CreateAsync((home, _, _, token) => fetch(home, token));

        public static async Task<Setup> CreateAsync(StreamingRateLimitsFetcher fetch)
        {
            var setup = new Setup();
            var paths = new AppPaths(new Dictionary<string, string?>
            {
                ["CODEX_HOME"] = Path.Combine(setup._root, "codex"),
                ["CODEX_SWITCH_DATA_HOME"] = Path.Combine(setup._root, "data"),
            });
            Directory.CreateDirectory(paths.CodexHome);
            await File.WriteAllTextAsync(paths.ConfigPath, "model_provider = \"openai\"\n");
            var files = new AtomicFileStore();
            var accounts = new AccountStore(paths.DataHome, new Protector(), files);
            await accounts.SaveAsync("Synthetic account", Encoding.UTF8.GetBytes(
                "{\"tokens\":{\"access_token\":\"synthetic-a\",\"refresh_token\":\"synthetic-r\",\"account_id\":\"synthetic-account\"}}"), default);
            var fetcher = new UsageFetcher(accounts, paths.ConfigPath, paths.TemporaryRoot, fetch);
            var usage = new UsageRefreshCoordinator(accounts, fetcher, new UsageCache(paths.UsageCachePath, files));
            setup.Services = new AppServices(paths, accounts, null!, null!, null!, usage, null!,
                new Sub2ApiProbe(0, TimeSpan.FromMilliseconds(20)), new StartupRegistration("CodexSwitch-Synthetic-Refresh-Test", "synthetic.exe"));
            return setup;
        }

        public MainForm CreateForm()
        {
            var form = new MainForm(Services, false, (_, error) => Errors.Add(error));
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();
            _ = form.Handle;
            return form;
        }

        public void EnableMaintenance()
            => Services = Services with { Maintenance = new DataMaintenanceService(Services.Paths.TemporaryRoot, Services.Paths.LogDirectory, Services.Paths.AccountBackupDirectory) };

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class Protector : ICredentialProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> value) => value.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> value) => value.ToArray();
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Synthetic refresh test did not finish");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
