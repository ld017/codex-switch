namespace CodexSwitch.Core.Configuration;

public enum ProviderConfigError
{
    DuplicateProvider,
    MissingProvider,
    MissingModelAnchor,
    UnknownProvider,
}

public sealed class ProviderConfigException : Exception
{
    public ProviderConfigException(ProviderConfigError error, string message)
        : base(message)
    {
        Error = error;
    }

    public ProviderConfigError Error { get; }
}
