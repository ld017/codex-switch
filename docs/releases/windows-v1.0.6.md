# Codex Switch for Windows 1.0.6

Windows 10/11 x64，自带 .NET 8 运行时。发布标签：`windows-v1.0.6`。

## 下载

- [安装版 CodexSwitch-Setup-1.0.6-x64.exe](https://github.com/ld017/codex-switch/releases/download/windows-v1.0.6/CodexSwitch-Setup-1.0.6-x64.exe)
- [免安装版 CodexSwitch-1.0.6-win-x64.zip](https://github.com/ld017/codex-switch/releases/download/windows-v1.0.6/CodexSwitch-1.0.6-win-x64.zip)
- [SHA256SUMS.txt](https://github.com/ld017/codex-switch/releases/download/windows-v1.0.6/SHA256SUMS.txt)

## 确认后自动切号

点击“切换账号”并确认后，程序自动关闭 Codex。等待正常退出 3 秒后仍有残留进程，将自动终止已核实身份的 Codex 进程树；确认退出后备份并写入目标账号，重新打开 Codex。无需先手动退出，正在运行的任务会被中断。

主面板、托盘、登录后立即切换和重新登录当前账号使用相同的关闭流程。确认框提前说明中断任务及强制退出行为，取消确认不执行关闭。Provider 切换保持正常退出策略。

不能核实进程身份、无法终止进程或无法确认退出时，不写入新凭据；目标账号验证、备份和失败恢复继续保留。强制关闭范围限定为已核实的 Codex 桌面进程树，不按进程名称全局终止。

## 验证与发布

Windows 完整测试通过：Core 193 项、Integration 93 项，共 286 项。覆盖正常退出、强制关闭、子进程退出竞态、PID 复用、自身保护、取消、权限失败及凭据写入/重启顺序。

关闭与切换流程使用虚构进程、故障注入和隔离账号进行测试；未在承载工作任务的 Codex 会话上执行真实强制切号。强制中断不会保证未完成任务的内容已保存。

安装器采用文件白名单；发布程序禁用调试符号并隐藏本机构建路径。发布包不包含用户账号、凭据、缓存、日志、备份或个人截图。程序尚未进行商业代码签名。
