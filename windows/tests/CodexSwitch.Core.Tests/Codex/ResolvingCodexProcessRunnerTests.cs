using CodexSwitch.Core.Codex;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class ResolvingCodexProcessRunnerTests
{
    [Fact]
    public async Task Running_switch_uses_new_cli_after_desktop_update()
    {
        using var directory = new TestDirectory();
        var local = directory.File("local");
        string Create(string version)
        {
            var path = Path.Combine(local, "OpenAI", "Codex", "bin", version, "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return path;
        }
        var old = Create("old");
        var environment = new Dictionary<string, string?> { ["LOCALAPPDATA"] = local };
        var recorder = new RecordingRunner();
        var runner = new ResolvingCodexProcessRunner(recorder, () => CodexCliResolver.Resolve(environment));
        var spec = new ProcessSpec(old, ["login"], new Dictionary<string, string?> { ["CODEX_HOME"] = "isolated" }, WorkingDirectory: "isolated");
        await runner.RunAsync(spec, default);
        File.Delete(old);
        var updated = Create("new");
        await runner.RunAsync(spec, default);
        Assert.Equal(new[] { old, updated }, recorder.Calls.Select(call => call.FileName));
        Assert.All(recorder.Calls, call => { Assert.Equal(spec.Arguments, call.Arguments); Assert.Equal(spec.Environment, call.Environment); Assert.Equal(spec.WorkingDirectory, call.WorkingDirectory); });
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Calls.Add(spec);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
