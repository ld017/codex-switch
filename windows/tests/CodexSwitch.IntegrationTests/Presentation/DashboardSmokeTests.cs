using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CodexSwitch.App;
using CodexSwitch.App.Accounts;
using CodexSwitch.App.Controls;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Presentation;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.IntegrationTests.Presentation;

public sealed class DashboardSmokeTests
{
    [Theory]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void Dynamically_created_account_card_scales_internal_fixed_geometry(float scale)
        => OnSta(() =>
        {
            using var card = new AccountUsageCard();
            card.Scale(new SizeF(scale, scale));
            card.CreateControl();
            card.PerformLayout();
            var root = (TableLayoutPanel)card.Controls[0];
            Assert.Equal(238 * scale, card.Height);
            Assert.Equal(36 * scale, root.RowStyles[0].Height);
            Assert.Equal(70 * scale, root.RowStyles[4].Height);
            Assert.Equal(16 * scale, card.Padding.Left);
            Assert.True(Field<Label>(card, "_monthTokens").Height >= 24 * scale);
        });

    [Fact]
    public void Account_card_binds_remaining_quota_and_preserves_cached_error_details()
        => OnSta(() =>
        {
            var now = DateTimeOffset.Now;
            var activity = Activity(now.AddDays(-1), 123_456_789, 1_200_000);
            var snapshot = Snapshot(now.AddDays(-1), 99, 44) with
            {
                TokenUsage = TokenUsageState.Failed("Synthetic token activity error", activity),
            };
            var presentation = AccountUsagePresentation.FromState(UsageState.Failed("Synthetic refresh error", snapshot), now);
            using var card = new AccountUsageCard { Width = 440 };

            card.Bind(Guid.NewGuid(), "Synthetic account", presentation, presentation.StatusText, true, "Synthetic refresh error");
            card.CreateControl();
            card.PerformLayout();

            var primary = Field<UsageBarControl>(card, "_primaryBar");
            var secondary = Field<UsageBarControl>(card, "_secondaryBar");
            var status = Field<Label>(card, "_status");
            var tooltip = Field<ToolTip>(card, "_details");
            Assert.Equal(1, primary.Percent);
            Assert.Equal(UsageLevel.Critical, primary.Level);
            Assert.Equal(56, secondary.Percent);
            Assert.Equal(UsageLevel.Healthy, secondary.Level);
            Assert.Equal("1%", Field<Label>(card, "_primaryPercent").Text);
            Assert.Contains("缓存", status.Text);
            Assert.Contains("刷新失败", status.Text);
            Assert.Contains("Synthetic refresh error", tooltip.GetToolTip(status));
            Assert.Contains("剩余", tooltip.GetToolTip(Field<Label>(card, "_primaryPercent")));
            Assert.Equal("待刷新", Field<Label>(card, "_primaryReset").Text);
            Assert.Equal("1.23 亿", Field<Label>(card, "_lifetimeTokens").Text);
            Assert.Equal("0.01 亿", Field<Label>(card, "_monthTokens").Text);
            var tokenStatus = Field<Label>(card, "_tokenStatus");
            Assert.Contains("缓存", tokenStatus.Text);
            Assert.Contains("Synthetic token activity error", tooltip.GetToolTip(tokenStatus));
            Assert.Contains("123,456,789", tooltip.GetToolTip(Field<Label>(card, "_lifetimeTokens")));
            Assert.Equal(238, card.Height);

            using var bitmap = new Bitmap(card.Width, card.Height);
            card.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        });

    [Fact]
    public void Synthetic_dashboard_renders_cards_without_initializing_services_or_touching_accounts()
        => OnSta(() =>
        {
            // Never show this form. Its Shown event initializes real services; this smoke test
            // intentionally supplies no usable service dependencies and renders synthetic state only.
            var services = new AppServices(null!, null!, null!, null!, null!, null!, null!, null!, null!);
            using var form = new MainForm(services, false);
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();
            var now = DateTimeOffset.Now;
            AccountProfile[] accounts = [
                new(Guid.NewGuid(), "工作账号 · 主用", "synthetic-1"),
                new(Guid.NewGuid(), "研究账号 · 备用", "synthetic-2"),
                new(Guid.NewGuid(), "个人账号 · 缓存记录", "synthetic-3"),
                new(Guid.NewGuid(), "新账号 · 等待登录", "synthetic-4"),
            ];
            var usage = new Dictionary<Guid, UsageState>
            {
                [accounts[0].Id] = UsageState.Available(Snapshot(now, 99, 44) with
                {
                    TokenUsage = TokenUsageState.Available(Activity(now, 1_234_567_890, 12_345_678)),
                }),
                [accounts[1].Id] = UsageState.Available(Snapshot(now, 0, 84) with
                {
                    TokenUsage = TokenUsageState.Available(Activity(now, 234_567_890, 2_345_678)),
                }),
                [accounts[2].Id] = UsageState.Available(Snapshot(now, 46, 74) with
                {
                    TokenUsage = TokenUsageState.Failed("合成测试：Token 请求失败，保留缓存", Activity(now.AddDays(-1), 100_000_000, 0)),
                }),
                [accounts[3].Id] = UsageState.LoginRequired("合成测试：没有任何真实凭据"),
            };
            SetField(form, "_accounts", accounts);
            SetField(form, "_usage", usage);
            var identities = Field<Dictionary<Guid, AuthIdentity>>(form, "_identities");
            for (var index = 0; index < accounts.Length; index++)
                identities[accounts[index].Id] = new AuthIdentity(AuthKind.OAuth, $"synthetic-account-{index}", $"synthetic-user-{index}");
            SetField(form, "_activeProfileId", accounts[0].Id);
            SetField(form, "_selectedProfileId", accounts[0].Id);
            Field<Label>(form, "_providerStatus").Text = "当前连接：OpenAI 官方（合成预览）";
            Field<Label>(form, "_sub2Status").Text = "Sub2API 服务：未探测";
            Field<Label>(form, "_refreshStatus").Text = "合成预览 · 未访问网络";
            Field<ToolStripStatusLabel>(form, "_statusText").Text = "此图仅验证界面，所有账号和额度均为合成数据";

            typeof(MainForm).GetMethod("RenderAccounts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            form.CreateControl();
            form.PerformLayout();
            var panel = Field<FlowLayoutPanel>(form, "_accountsPanel");
            Assert.Equal(4, panel.Controls.OfType<AccountUsageCard>().Count());
            Assert.True(panel.AutoScroll);
            Assert.Equal("15.69", Field<Label>(form, "_totalTokens").Text);
            Assert.Equal("0.15", Field<Label>(form, "_monthTokens").Text);
            Assert.Contains("累计 3/4", Field<Label>(form, "_tokenCoverage").Text);
            Assert.Contains("本月 3/4", Field<Label>(form, "_tokenCoverage").Text);
            Assert.Contains("缓存", Field<Label>(form, "_tokenCoverage").Text);
            foreach (var card in panel.Controls.OfType<AccountUsageCard>())
            {
                Assert.True(card.Right <= panel.ClientSize.Width, $"Card clipped horizontally: {card.Bounds}, panel: {panel.ClientSize}");
                Assert.True(card.Bottom <= Math.Max(panel.DisplayRectangle.Height, panel.ClientSize.Height),
                    $"Card outside scrollable content: {card.Bounds}, panel content: {panel.DisplayRectangle}");
            }
            var layout = Field<TableLayoutPanel>(form, "_root");
            var actions = layout.GetControlFromPosition(0, 4)!;
            Assert.True(actions.Bottom <= layout.ClientSize.Height - layout.Padding.Bottom,
                $"Footer clipped vertically: {actions.Bounds}, root: {layout.ClientSize}");

            // A hidden Form reports all descendants as invisible and DrawToBitmap skips them.
            // Detaching its content temporarily makes the synthetic controls drawable without
            // showing the form or invoking its service-initializing Shown event.
            var originalIndex = form.Controls.GetChildIndex(layout);
            var originalBounds = layout.Bounds;
            form.Controls.Remove(layout);
            try
            {
                layout.CreateControl();
                layout.PerformLayout();
                foreach (var card in panel.Controls.OfType<AccountUsageCard>())
                {
                    foreach (var name in new[] { "_primaryPercent", "_secondaryPercent", "_lifetimeTokens", "_monthTokens" })
                    {
                        var label = Field<Label>(card, name);
                        var measured = TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, TextFormatFlags.SingleLine);
                        Assert.True(measured.Width <= label.ClientSize.Width,
                            $"Text clipped in {name}: '{label.Text}', measured {measured.Width}, available {label.ClientSize.Width}");
                    }
                }

                using var bitmap = new Bitmap(layout.Width, Math.Min(layout.Height, actions.Bottom + layout.Padding.Bottom));
                layout.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                // Guard against accidentally delivering an empty hidden-window bitmap again.
                Assert.NotEqual(bitmap.GetPixel(0, 0), bitmap.GetPixel(30, panel.Top + 20));
                if (Environment.GetEnvironmentVariable("CODEX_SWITCH_PREVIEW_PATH") is { Length: > 0 } path)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    bitmap.Save(path, ImageFormat.Png);
                }
            }
            finally
            {
                form.Controls.Add(layout);
                form.Controls.SetChildIndex(layout, originalIndex);
                layout.Bounds = originalBounds;
            }
        });

    [Fact]
    public void Many_synthetic_accounts_remain_reachable_by_scrolling_while_actions_stay_visible()
        => OnSta(() =>
        {
            var services = new AppServices(null!, null!, null!, null!, null!, null!, null!, null!, null!);
            using var form = new MainForm(services, false);
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();
            var accounts = Enumerable.Range(1, 12).Select(index =>
                new AccountProfile(Guid.NewGuid(), $"合成账号 {index}", $"synthetic-{index}")).ToArray();
            SetField(form, "_accounts", accounts);
            typeof(MainForm).GetMethod("RenderAccounts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            form.CreateControl();
            form.PerformLayout();
            var root = Field<TableLayoutPanel>(form, "_root");
            form.Controls.Remove(root);
            try
            {
                root.CreateControl();
                root.PerformLayout();
                var panel = Field<FlowLayoutPanel>(form, "_accountsPanel");
                var cards = panel.Controls.OfType<AccountUsageCard>().ToArray();
                Assert.Equal(12, cards.Length);
                Assert.True(panel.AutoScroll);
                Assert.True(panel.DisplayRectangle.Height > panel.ClientSize.Height);
                Assert.True(cards[^1].Bottom > panel.ClientSize.Height);
                Assert.All(cards, card => Assert.True(card.Right <= panel.ClientSize.Width,
                    $"Card clipped horizontally: {card.Bounds}; viewport {panel.ClientSize}"));
                var scale = form.DeviceDpi / 96d;
                var screen = Screen.FromControl(form).WorkingArea;
                var plan = DashboardLayoutPlan.Create(accounts.Length, (int)(screen.Width / scale), (int)(screen.Height / scale));
                Assert.Equal(plan.Columns, cards.Select(card => card.Left).Distinct().Count());
                var actions = root.GetControlFromPosition(0, 4)!;
                Assert.True(actions.Bottom <= root.ClientSize.Height - root.Padding.Bottom);
                Assert.True(actions.Top >= panel.Bottom);
                panel.ScrollControlIntoView(cards[^1]);
                Assert.True(cards[^1].Top < panel.ClientSize.Height && cards[^1].Bottom > 0,
                    $"Last account remains unreachable: {cards[^1].Bounds}; viewport {panel.ClientSize}");
            }
            finally { form.Controls.Add(root); }
        });

    [Fact]
    public void Repeated_usage_updates_preserve_cards_handles_selection_scroll_and_user_window_bounds()
        => OnSta(() =>
        {
            using var form = SyntheticForm();
            var now = DateTimeOffset.Now;
            var accounts = SyntheticAccounts(10);
            var usage = accounts.ToDictionary(account => account.Id,
                _ => UsageState.Available(Snapshot(now, 20, 40)));
            SetField(form, "_accounts", accounts);
            SetField(form, "_usage", usage);
            Render(form);
            var scale = form.DeviceDpi / 96d;
            form.Size = new Size((int)Math.Round(550 * scale), form.Height);
            form.Location = new Point(form.Left + 3, form.Top + 4);

            WithDetachedContent(form, _ =>
            {
                var panel = Field<FlowLayoutPanel>(form, "_accountsPanel");
                var cards = panel.Controls.OfType<AccountUsageCard>().ToDictionary(card => card.ProfileId);
                var handles = cards.ToDictionary(pair => pair.Key, pair => pair.Value.Handle);
                Invoke(form, "SelectAccount", accounts[4].Id);
                panel.ScrollControlIntoView(cards[accounts[6].Id]);
                Assert.NotEqual(Point.Empty, panel.AutoScrollPosition);
                var scroll = panel.AutoScrollPosition;
                var bounds = form.Bounds;
                var unchanged = Field<Label>(cards[accounts[1].Id], "_primaryPercent");
                var unchangedTextUpdates = 0;
                unchanged.TextChanged += (_, _) => unchangedTextUpdates++;

                for (var update = 0; update < 5; update++)
                {
                    usage[accounts[0].Id] = UsageState.Available(Snapshot(now, 10 + update, 50));
                    Render(form);

                    var updatedCards = panel.Controls.OfType<AccountUsageCard>().ToDictionary(card => card.ProfileId);
                    Assert.Equal(cards.Count, updatedCards.Count);
                    foreach (var (id, originalCard) in cards)
                    {
                        Assert.Same(originalCard, updatedCards[id]);
                        Assert.Equal(handles[id], updatedCards[id].Handle);
                        Assert.False(originalCard.IsDisposed);
                    }
                    Assert.Equal(scroll, panel.AutoScrollPosition);
                    Assert.Equal(bounds, form.Bounds);
                    Assert.Equal(accounts[4].Id, Field<Guid?>(form, "_selectedProfileId"));
                    Assert.True(cards[accounts[4].Id].Selected);
                    Assert.Single(cards.Values, card => card.Selected);
                    Assert.Equal($"{90 - update}%", Field<Label>(cards[accounts[0].Id], "_primaryPercent").Text);
                    Assert.Equal("50%", Field<Label>(cards[accounts[0].Id], "_secondaryPercent").Text);
                    Assert.Equal("80%", unchanged.Text);
                }
                Assert.Equal(0, unchangedTextUpdates);
            });
        });

    [Fact]
    public void Adding_removing_and_reordering_accounts_only_changes_affected_cards()
        => OnSta(() =>
        {
            using var form = SyntheticForm();
            var accounts = SyntheticAccounts(4);
            SetField(form, "_accounts", accounts);
            Render(form);

            WithDetachedContent(form, _ =>
            {
                var panel = Field<FlowLayoutPanel>(form, "_accountsPanel");
                var originals = panel.Controls.OfType<AccountUsageCard>().ToDictionary(card => card.ProfileId);
                var handles = originals.ToDictionary(pair => pair.Key, pair => pair.Value.Handle);
                var bounds = form.Bounds;
                var added = new AccountProfile(Guid.NewGuid(), "合成新增账号", "synthetic-added");
                var expanded = accounts.Append(added).ToArray();
                SetField(form, "_accounts", expanded);
                Render(form);

                Assert.Equal(5, panel.Controls.OfType<AccountUsageCard>().Count());
                foreach (var card in panel.Controls.OfType<AccountUsageCard>().Where(card => originals.ContainsKey(card.ProfileId)))
                {
                    Assert.Same(originals[card.ProfileId], card);
                    Assert.Equal(handles[card.ProfileId], card.Handle);
                }
                var addedCard = panel.Controls.OfType<AccountUsageCard>().Single(card => card.ProfileId == added.Id);
                var addedHandle = addedCard.Handle;
                var remaining = new[] { added, accounts[3], accounts[0], accounts[2] };
                SetField(form, "_accounts", remaining);
                Render(form);

                var actual = panel.Controls.OfType<AccountUsageCard>().ToArray();
                Assert.Equal(remaining.Select(account => account.Id), actual.Select(card => card.ProfileId));
                Assert.True(originals[accounts[1].Id].IsDisposed);
                Assert.Same(addedCard, actual[0]);
                Assert.Equal(addedHandle, actual[0].Handle);
                foreach (var card in actual.Skip(1))
                {
                    Assert.Same(originals[card.ProfileId], card);
                    Assert.Equal(handles[card.ProfileId], card.Handle);
                }
                Assert.Equal(bounds, form.Bounds);
            });
        });

    [Fact]
    public void Dashboard_defaults_to_a_compact_portrait_window_inside_the_working_area()
        => OnSta(() =>
        {
            using var form = SyntheticForm();
            SetField(form, "_accounts", SyntheticAccounts(4));
            Render(form);
            var scale = form.DeviceDpi / 96d;
            var screen = Screen.FromControl(form).WorkingArea;
            Assert.InRange(form.Width / scale, 280, 560);
            Assert.True(form.Height > form.Width, $"Default window is not portrait: {form.Size}");
            Assert.True(screen.Contains(form.Bounds), $"Default window {form.Bounds} is outside working area {screen}");
        });

    [Fact]
    public void Account_list_remains_one_vertical_column_when_the_user_widens_the_window()
        => OnSta(() =>
        {
            using var form = SyntheticForm();
            SetField(form, "_accounts", SyntheticAccounts(6));
            Render(form);
            form.Width = (int)Math.Round(1150 * form.DeviceDpi / 96d);

            WithDetachedContent(form, _ =>
            {
                Invoke(form, "LayoutAccountCards");
                var panel = Field<FlowLayoutPanel>(form, "_accountsPanel");
                panel.PerformLayout();
                var cards = panel.Controls.OfType<AccountUsageCard>().ToArray();
                Assert.Equal(6, cards.Length);
                Assert.Single(cards.Select(card => card.Left).Distinct());
                for (var index = 1; index < cards.Length; index++)
                    Assert.True(cards[index].Top >= cards[index - 1].Bottom,
                        $"Accounts are not in one vertical list: {cards[index - 1].Bounds}, {cards[index].Bounds}");
                Assert.All(cards, card => Assert.True(card.Right <= panel.ClientSize.Width,
                    $"Card clipped horizontally: {card.Bounds}; viewport {panel.ClientSize}"));
            });
        });

    [Fact]
    public void Token_details_show_exact_daily_records_and_switch_to_unknown_without_real_services()
        => OnSta(() =>
        {
            var now = DateTimeOffset.Now;
            var today = now.LocalDateTime.ToString("yyyy-MM-dd");
            var oldDate = new DateOnly(now.Year, now.Month, 1).AddDays(-1).ToString("yyyy-MM-dd");
            AccountProfile[] accounts = [new(Guid.NewGuid(), "合成 Token 缓存", "one"), new(Guid.NewGuid(), "合成未获取", "two")];
            var activity = new TokenUsageSnapshot(new TokenUsageSummary(123_456_789),
                [new(today, 1_234_567), new(today, 1_000_000), new(oldDate, 50),
                    new(now.LocalDateTime.AddDays(1).ToString("yyyy-MM-dd"), 9), new("invalid", 9), new(today, -1)], now.AddHours(-2));
            var usage = new Dictionary<Guid, UsageState>
            {
                [accounts[0].Id] = UsageState.Available(Snapshot(now, 10, 20) with
                {
                    TokenUsage = TokenUsageState.Failed("合成 Token 活动刷新失败", activity),
                }),
                [accounts[1].Id] = UsageState.Available(Snapshot(now, 10, 20) with
                {
                    TokenUsage = TokenUsageState.Failed("合成：服务未返回统计"),
                }),
            };
            using var form = new TokenUsageDetailsForm(accounts, usage, accounts[0].Id);
            form.CreateControl();
            form.PerformLayout();
            var totals = Field<Label>(form, "_totals");
            var source = Field<Label>(form, "_source");
            var days = Field<DataGridView>(form, "_days");
            Assert.Contains("123,456,789", totals.Text);
            Assert.Contains("1,234,567", totals.Text);
            Assert.Contains("缓存", source.Text);
            Assert.Contains("合成 Token 活动刷新失败", source.Text);
            Assert.Equal(2, days.Rows.Count);
            Assert.Equal(today, days.Rows[0].Cells[0].Value);
            Assert.Equal("1,234,567", days.Rows[0].Cells[2].Value);
            Assert.Equal(oldDate, days.Rows[1].Cells[0].Value);

            var root = (TableLayoutPanel)form.Controls[0];
            form.Controls.Remove(root);
            try
            {
                root.CreateControl();
                root.PerformLayout();
                using var bitmap = new Bitmap(root.Width, root.Height);
                root.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                if (Environment.GetEnvironmentVariable("CODEX_SWITCH_DETAILS_PREVIEW_PATH") is { Length: > 0 } path)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    bitmap.Save(path, ImageFormat.Png);
                }
            }
            finally { form.Controls.Add(root); }

            Field<ComboBox>(form, "_account").SelectedIndex = 1;
            Assert.Contains("未获取", totals.Text);
            Assert.Contains("服务未返回统计", source.Text);
            Assert.Empty(days.Rows.Cast<DataGridViewRow>());
        });

    [Fact]
    public void Closing_waits_for_cancelled_refresh_cleanup_before_disposing_the_hidden_form()
        => OnSta(() =>
        {
            var services = new AppServices(null!, null!, null!, null!, null!, null!, null!, null!, null!);
            using var form = new MainForm(services, false);
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();
            _ = form.Handle;
            var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetField(form, "_refreshing", true);
            SetField(form, "_refreshTask", cleanup.Task);
            var args = new FormClosingEventArgs(CloseReason.UserClosing, false);

            typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [args]);

            Assert.True(args.Cancel);
            Assert.True(Field<CancellationTokenSource>(form, "_lifetime").IsCancellationRequested);
            Assert.False(form.IsDisposed);
            cleanup.SetResult();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!form.IsDisposed && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Assert.True(form.IsDisposed, "Form did not close after the pending cleanup completed.");
        });

    private static UsageSnapshot Snapshot(DateTimeOffset fetchedAt, int primaryUsed, int secondaryUsed) => new(
        "plus", null, primaryUsed, fetchedAt.AddHours(2), 300,
        secondaryUsed, fetchedAt.AddDays(4), 10_080, 2, fetchedAt.AddDays(7), fetchedAt);

    private static TokenUsageSnapshot Activity(DateTimeOffset fetchedAt, long lifetime, long monthly)
        => new(new TokenUsageSummary(lifetime), [new(DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-01"), monthly)], fetchedAt);

    private static MainForm SyntheticForm()
    {
        // These tests never show a form or provide live services, so no account files,
        // authentication material, subprocesses, or network requests can be accessed.
        var services = new AppServices(null!, null!, null!, null!, null!, null!, null!, null!, null!);
        var form = new MainForm(services, false);
        Field<System.Windows.Forms.Timer>(form, "_timer").Stop();
        _ = form.Handle;
        return form;
    }

    private static AccountProfile[] SyntheticAccounts(int count) => Enumerable.Range(1, count)
        .Select(index => new AccountProfile(Guid.NewGuid(), $"合成账号 {index}", $"synthetic-{index}")).ToArray();

    private static void Render(MainForm form)
    {
        Invoke(form, "RenderAccounts");
        form.PerformLayout();
    }

    private static void Invoke(object instance, string method, params object?[] arguments)
        => instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);

    private static void WithDetachedContent(MainForm form, Action<TableLayoutPanel> action)
    {
        var root = Field<TableLayoutPanel>(form, "_root");
        var bounds = root.Bounds;
        form.Controls.Remove(root);
        try
        {
            root.Bounds = bounds;
            root.CreateControl();
            root.PerformLayout();
            action(root);
        }
        finally { form.Controls.Add(root); }
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Synthetic WinForms render did not finish within 20 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
