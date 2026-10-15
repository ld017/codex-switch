using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Presentation;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Presentation;

public sealed class TrayMenuModelTests
{
    [Fact]
    public void Build_marks_active_provider_and_account_and_formats_reset_credits()
    {
        var active = new AccountProfile(Guid.NewGuid(), "工作", "fingerprint");
        var other = new AccountProfile(Guid.NewGuid(), "个人", "fingerprint-2");
        var usage = UsageState.Available(new UsageSnapshot("pro", null, 12, null, 300, 34, null, 10_080, 2, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow));
        var snapshot = new AppSnapshot(Provider.OpenAI, true, active.Id, [active, other], new Dictionary<Guid, UsageState> { [active.Id] = usage }, false);

        var menu = TrayMenuModel.Build(snapshot);

        Assert.True(menu.ProviderItems.Single(x => x.Provider == Provider.OpenAI).Checked);
        Assert.True(menu.AccountItems.Single(x => x.ProfileId == active.Id).Checked);
        Assert.Contains("主剩余 88%", menu.AccountItems.Single(x => x.ProfileId == active.Id).Detail);
        Assert.Contains("次剩余 66%", menu.AccountItems.Single(x => x.ProfileId == active.Id).Detail);
        Assert.Contains("重置卡 2", menu.AccountItems.Single(x => x.ProfileId == active.Id).Detail);
    }

    [Fact]
    public void Build_disables_mutations_during_operation()
    {
        var snapshot = new AppSnapshot(Provider.OpenAI, false, null, [], new Dictionary<Guid, UsageState>(), true);

        var menu = TrayMenuModel.Build(snapshot);

        Assert.False(menu.MutationsEnabled);
        Assert.Contains("离线", menu.Sub2ApiStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UsageStatus.Error, "刷新失败")]
    [InlineData(UsageStatus.Refreshing, "刷新中")]
    [InlineData(UsageStatus.AuthenticationRequired, "登录已失效")]
    public void Build_labels_retained_snapshot_as_cache_when_it_is_not_current(UsageStatus status, string message)
    {
        var account = new AccountProfile(Guid.NewGuid(), "工作", "fingerprint");
        var usage = new UsageState(status, new UsageSnapshot("pro", null, 12, null, 300, null, null, null, null, null, DateTimeOffset.UnixEpoch), "error");
        var snapshot = new AppSnapshot(Provider.OpenAI, true, account.Id, [account], new Dictionary<Guid, UsageState> { [account.Id] = usage }, false);

        var detail = TrayMenuModel.Build(snapshot).AccountItems.Single().Detail;

        Assert.Contains("主剩余 88%", detail);
        Assert.Contains("缓存", detail);
        Assert.Contains(message, detail);
        Assert.DoesNotContain("次剩余", detail);
    }
}
