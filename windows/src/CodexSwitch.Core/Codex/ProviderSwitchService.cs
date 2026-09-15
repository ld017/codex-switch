using System.Text;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Codex;

public enum ProviderSwitchStage
{
    Prerequisites,
    Backup,
    Write,
    Validation,
    Recovery,
}

public sealed class ProviderSwitchException : Exception
{
    public ProviderSwitchException(ProviderSwitchStage stage, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
    }

    public ProviderSwitchStage Stage { get; }
}

public sealed record ProviderSwitchResult(Provider Previous, Provider Current, string BackupPath);

public sealed class ProviderSwitchService
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly string _configPath;
    private readonly string _codexCliPath;
    private readonly IAtomicFileStore _files;
    private readonly BackupManager _backups;
    private readonly IProcessRunner _processRunner;

    public ProviderSwitchService(
        string configPath,
        string codexCliPath,
        IAtomicFileStore files,
        BackupManager backups,
        IProcessRunner processRunner)
    {
        _configPath = configPath;
        _codexCliPath = codexCliPath;
        _files = files;
        _backups = backups;
        _processRunner = processRunner;
    }

    public async Task<ProviderSwitchResult> SwitchAsync(Provider target, CancellationToken cancellationToken)
    {
        var originalBytes = _files.ReadAllBytes(_configPath);
        var source = Utf8WithoutBom.GetString(originalBytes);
        var previous = ProviderConfigEditor.Read(source);
        if (target == Provider.Sub2Api && !ProviderConfigEditor.HasSub2ApiConfiguration(source))
        {
            throw new ProviderSwitchException(
                ProviderSwitchStage.Prerequisites,
                "共享配置中缺少完整的 Sub2API 定义。");
        }

        string backupPath;
        try
        {
            backupPath = _backups.CreateBackup(_configPath);
        }
        catch (Exception error)
        {
            throw new ProviderSwitchException(ProviderSwitchStage.Backup, "无法备份 Codex 配置。", error);
        }

        try
        {
            var updated = ProviderConfigEditor.Replace(source, target);
            _files.WriteAtomically(_configPath, Utf8WithoutBom.GetBytes(updated));
        }
        catch (Exception error)
        {
            RestoreOrThrow(backupPath, originalBytes, error);
            throw new ProviderSwitchException(ProviderSwitchStage.Write, "无法安全写入 Codex 配置。", error);
        }

        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessSpec(_codexCliPath, ["doctor", "--json"], Timeout: TimeSpan.FromSeconds(30)),
                cancellationToken);
            if (result.ExitCode != 0 || !DoctorReport.IsConfigValid(result.StandardOutput))
            {
                throw new InvalidDataException("Codex doctor rejected the configuration.");
            }
        }
        catch (Exception error) when (error is not ProviderSwitchException)
        {
            RestoreOrThrow(backupPath, originalBytes, error);
            throw new ProviderSwitchException(ProviderSwitchStage.Validation, "Codex 无法验证修改后的配置。", error);
        }

        return new ProviderSwitchResult(previous, target, backupPath);
    }

    private void RestoreOrThrow(string backupPath, byte[] expected, Exception originalError)
    {
        try
        {
            var backup = _files.ReadAllBytes(backupPath);
            _files.WriteAtomically(_configPath, backup);
            if (!_files.ReadAllBytes(_configPath).SequenceEqual(expected))
            {
                throw new InvalidDataException("Restored configuration differs from the original bytes.");
            }
        }
        catch (Exception recoveryError)
        {
            throw new ProviderSwitchException(
                ProviderSwitchStage.Recovery,
                "切换失败，并且无法验证原配置已恢复。",
                new AggregateException(originalError, recoveryError));
        }
    }
}
