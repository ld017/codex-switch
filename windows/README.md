# Codex Switch for Windows

Windows 10/11 x64 原生托盘版，提供 Provider 切换、多个 OpenAI 账号登录/切换和逐账号额度监控。

## 安装

从 GitHub Actions 下载 `CodexSwitch-Setup-x64.exe`，运行后按向导安装。安装程序按当前用户安装到 `%LOCALAPPDATA%\Programs\Codex Switch`，创建开始菜单快捷方式，并可选登录 Windows 时自动启动。程序自带 .NET 8 运行时。

需要先安装官方 Codex Windows 应用。程序会从 `CODEX_CLI`、`PATH` 或 `%LOCALAPPDATA%\OpenAI\Codex\bin\*\codex.exe` 定位 CLI。

## 使用

右击系统托盘图标可以：

- 查看和切换 OpenAI / Sub2API Provider。
- 登录、切换、重命名或删除 OpenAI 账号。
- 查看每个账号的主/次额度、重置时间和重置卡。
- 手动刷新额度、打开 Codex、设置开机启动或查看日志。

Provider 或账号切换会重启 Codex，确认框会提前提示正在运行的任务可能中断。Sub2API 必须已配置在 `config.toml`，且本机 `127.0.0.1:8080` 在线；Windows 版不会自动启动 Sub2API 服务。

## 数据与安全

- Codex 配置与实时登录：`%USERPROFILE%\.codex`（或显式 `CODEX_HOME`）。
- 应用数据：`%LOCALAPPDATA%\CodexSwitch`。
- 账号凭据：使用 Windows DPAPI CurrentUser 加密后保存。
- Provider 备份：`%USERPROFILE%\.codex\provider-switcher\backups`，保留最近 10 份。
- 日志：`%LOCALAPPDATA%\CodexSwitch\logs`，Token 和认证字段会脱敏。

卸载默认保留加密账号和设置，卸载向导会询问是否一并删除。

## 本地构建

需要 .NET 8 SDK 和 Inno Setup 6：

```powershell
dotnet test windows/CodexSwitch.sln -c Release
powershell -ExecutionPolicy Bypass -File windows/scripts/build-installer.ps1
```

产物：

- `artifacts/publish/CodexSwitch.exe`
- `artifacts/installer/CodexSwitch-Setup-x64.exe`

## 当前限制

- 首版仅支持 Windows 10/11 x64。
- 不包含 macOS 的共享历史代理和 URL Scheme。
- 不跨 Windows 用户或平台同步账号。
