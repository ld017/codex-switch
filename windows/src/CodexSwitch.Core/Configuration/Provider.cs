namespace CodexSwitch.Core.Configuration;

public enum Provider
{
    OpenAI,
    Sub2Api,
}

public static class ProviderExtensions
{
    public static string ToConfigValue(this Provider provider) => provider switch
    {
        Provider.OpenAI => "openai",
        Provider.Sub2Api => "sub2api",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };
}
