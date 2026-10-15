# Codex Switch for Windows 1.0.5

Windows 10/11 x64，自带 .NET 8 运行时。发布标签：`windows-v1.0.5`。

## 下载

- [安装版 CodexSwitch-Setup-1.0.5-x64.exe](https://github.com/ld017/codex-switch/releases/download/windows-v1.0.5/CodexSwitch-Setup-1.0.5-x64.exe)
- [免安装版 CodexSwitch-1.0.5-win-x64.zip](https://github.com/ld017/codex-switch/releases/download/windows-v1.0.5/CodexSwitch-1.0.5-win-x64.zip)
- [SHA256SUMS.txt](https://github.com/ld017/codex-switch/releases/download/windows-v1.0.5/SHA256SUMS.txt)

仍需安装官方 Codex Windows 应用。免安装版解压后运行 `CodexSwitch.exe`；请勿同时启动多个副本。发布程序未进行商业代码签名。

## 本次更新

- 加固切号确认后的异步事件异常处理，覆盖按钮、菜单、初始化和后台刷新；日志写入或错误提示失败不再继续抛错。
- 进程枚举与退出检测遇到短暂竞态时返回明确的失败原因。不能确认 Codex 完整退出时取消切换，不强杀进程。
- 切换阶段、进程启动和正常退出记录写入本机日志；常规异常生成脱敏异常日志，便于定位偶发退出。
- 保留窄长条单列界面、刷新复用卡片、累计/本月 Token、账号去重合计、每日记录与高 DPI 布局修复。

## 验证与边界

- Windows 完整测试：Core 181 项、Integration 66 项，共 247 项通过。
- 新增隔离故障注入覆盖确认后立即失败、进程读取失败不改凭据、日志写入失败、`await` 后异常、错误提示器失败。
- 历史偶发闪退尚未取得明确复现，不能保证本版消除了所有退出原因。真实账号切换未在承载工作任务的 Codex 中强行执行。
- Token 采用服务返回的 Codex 活动统计，可能延迟；缺失值不视为零，且不代表所有 ChatGPT 或 Sub2API 消耗。

## 发布包隐私

- 安装器采用明确文件白名单，只安装程序。
- 程序以禁用调试符号、隐藏本机构建路径的参数强制重建，避免沿用测试编译中的调试路径。
- 发布包不包含用户账号、登录凭据、运行缓存、日志、备份、转储或个人截图；界面示例仅使用合成数据。
- 下载后可使用 `Get-FileHash <文件名> -Algorithm SHA256` 对照 `SHA256SUMS.txt`。
