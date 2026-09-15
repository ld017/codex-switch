namespace CodexSwitch.Core.Codex;

public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    bool Visible = false);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
