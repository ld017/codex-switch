using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Presentation;

public sealed record AppSnapshot(Provider Provider, bool Sub2ApiOnline, Guid? ActiveProfileId, IReadOnlyList<AccountProfile> Accounts, IReadOnlyDictionary<Guid, UsageState> Usage, bool IsBusy);
public sealed record ProviderMenuItem(Provider Provider, string Text, bool Checked, bool Enabled);
public sealed record AccountMenuItem(Guid ProfileId, string Label, string Detail, bool Checked, bool Enabled);
public sealed record TrayMenu(IReadOnlyList<ProviderMenuItem> ProviderItems, IReadOnlyList<AccountMenuItem> AccountItems, string Sub2ApiStatus, bool MutationsEnabled);

public static class TrayMenuModel
{
    public static TrayMenu Build(AppSnapshot snapshot)
    {
        var enabled = !snapshot.IsBusy;
        var providers = new[]
        {
            new ProviderMenuItem(Provider.OpenAI, "OpenAI 官方", snapshot.Provider == Provider.OpenAI, enabled && snapshot.Provider != Provider.OpenAI),
            new ProviderMenuItem(Provider.Sub2Api, "Sub2API", snapshot.Provider == Provider.Sub2Api, enabled && snapshot.Provider != Provider.Sub2Api),
        };
        var accounts = snapshot.Accounts.Select(account =>
        {
            snapshot.Usage.TryGetValue(account.Id, out var usage);
            return new AccountMenuItem(account.Id, account.Label, FormatUsage(usage), snapshot.ActiveProfileId == account.Id, enabled && snapshot.Provider == Provider.OpenAI && snapshot.ActiveProfileId != account.Id);
        }).ToArray();
        return new TrayMenu(providers, accounts, snapshot.Sub2ApiOnline ? "Sub2API 服务：在线" : "Sub2API 服务：离线", enabled);
    }

    private static string FormatUsage(UsageState? state)
    {
        if (state is null) return "额度：尚未刷新";
        if (state.Status == UsageStatus.Refreshing && state.Snapshot is null) return "额度：刷新中…";
        if (state.Status == UsageStatus.AuthenticationRequired && state.Snapshot is null) return "登录已失效";
        if (state.Status == UsageStatus.Error && state.Snapshot is null) return "额度：刷新失败";
        var snapshot = state.Snapshot;
        if (snapshot is null) return "额度：不可用";
        var parts = new List<string>();
        if (snapshot.PrimaryRemainingPercent is { } primary) parts.Add($"主剩余 {primary}%");
        if (snapshot.SecondaryRemainingPercent is { } secondary) parts.Add($"次剩余 {secondary}%");
        if (snapshot.ResetCreditsAvailable is { } credits) parts.Add($"重置卡 {credits}");
        if (snapshot.CreditsUnlimited == true) parts.Add("积分不限");
        else if (snapshot.CreditsRemaining is { } balance) parts.Add($"积分 {balance:0.##}");
        if (state.Status != UsageStatus.Available)
        {
            parts.Add($"缓存 {snapshot.FetchedAt.LocalDateTime:M/d HH:mm}");
            parts.Add(state.Status switch
            {
                UsageStatus.Refreshing => "刷新中…",
                UsageStatus.AuthenticationRequired => "登录已失效",
                _ => "刷新失败",
            });
        }
        return parts.Count == 0 ? "额度：不可用" : string.Join(" · ", parts);
    }
}
