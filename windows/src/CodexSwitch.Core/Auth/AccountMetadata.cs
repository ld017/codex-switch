namespace CodexSwitch.Core.Auth;

public sealed record AccountProfile(Guid Id, string Label, string IdentityFingerprint);

public sealed record AccountConfiguration(
    int SchemaVersion,
    Guid? ActiveProfileId,
    IReadOnlyList<AccountProfile> Profiles)
{
    public static AccountConfiguration Empty { get; } = new(1, null, []);
}
