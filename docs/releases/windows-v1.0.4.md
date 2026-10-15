# Codex Switch for Windows 1.0.4

Windows 10/11 x64。发布标签：`windows-v1.0.4`。

## 下载

- [安装版 CodexSwitch-Setup-1.0.4-x64.exe](https://github.com/ld017/codex-switch/releases/download/windows-v1.0.4/CodexSwitch-Setup-1.0.4-x64.exe)
- [免安装版 CodexSwitch-1.0.4-win-x64.zip](https://github.com/ld017/codex-switch/releases/download/windows-v1.0.4/CodexSwitch-1.0.4-win-x64.zip)
- [SHA256SUMS.txt](https://github.com/ld017/codex-switch/releases/download/windows-v1.0.4/SHA256SUMS.txt)

两种版本均自带 .NET 8 运行时；仍需安装官方 Codex Windows 应用。免安装版解压后运行 `CodexSwitch.exe`，请勿同时启动多个副本。

## 本次更新

- 修复 Codex 更新后旧 CLI 路径失效；每次启动查询或登录进程时重新定位 CLI。
- 额度显示统一为剩余百分比，优先读取 Codex 对应的额度桶。失败明确保留缓存日期与错误原因，不将失败显示为已刷新。
- 每分钟自动刷新，最多 3 个账号并发；区分账号超时、用户取消和登录失效，保留已完成账号的结果。
- 新增累计/本月 Token、按账号身份去重的合计、覆盖数量，以及精确数量和每日记录详情。Token 查询失败不会丢弃已取得额度。
- 当前账号用实时 access token 查询，隔离进程不取得其 refresh token；其他账号的凭据轮换即使查询失败或取消也会保存。
- 切换前校验账号并备份；按 Codex 应用包身份识别桌面进程，正常退出并等待完整进程树。退出超时取消切换，不强杀正在运行的任务。
- 默认改为窄长条单列布局，保留滚动、选中账号和用户调整的窗口位置尺寸。刷新仅更新变化数据，不再清空重建全部卡片。
- 修复高 DPI 下累计用量、连接区和按钮的文字遮挡；标题、数字与说明分区，按实际字体高度排版。

Token 采用官方 Codex 活动统计，可能延迟，不等同普通 ChatGPT 聊天、Sub2API 用量或完整计费账单。本月按本机自然月汇总已返回的有效日记录；缺失不视为零，同日重复记录取较大值。详见 [Windows 使用说明](../../windows/README.md)。

## 验证

- 完整 Windows 测试：Core 181 项、Integration 58 项，共 239 项通过。
- 覆盖凭据隔离与恢复、RPC 超时/取消、额度与 Token 缓存、去重/月界、卡片复用、滚动与窗口保留、重绘稳定性及字体缩放布局。
- 已在 Windows 实际窗口检查累计与本月数字、连接区和操作区的显示。真实账号切换未在承载当前工作任务的 Codex 中执行；切换流程使用合成数据及只读进程识别验证。

## 文件校验

下载 `SHA256SUMS.txt`，在 PowerShell 使用 `Get-FileHash <下载文件> -Algorithm SHA256`，与对应行的散列值比较。

程序继续使用当前 Windows 用户已有的 Codex Switch 账号目录，发布包不包含账号凭据、日志或运行缓存。
