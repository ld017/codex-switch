# Codex Switch for Windows

Windows 10/11 x64 原生桌面面板版，提供 Provider 切换、多个 OpenAI 账号登录/切换和逐账号额度监控。

## 安装

从 [Windows 1.0.4 Release](https://github.com/ld017/codex-switch/releases/tag/windows-v1.0.4) 下载 `CodexSwitch-Setup-1.0.4-x64.exe` 并安装，或解压免安装 ZIP 后运行 `CodexSwitch.exe`。安装程序按当前用户安装到 `%LOCALAPPDATA%\Programs\Codex Switch`，创建开始菜单快捷方式，并可选登录 Windows 时自动启动。程序自带 .NET 8 运行时。Release 同时提供 `SHA256SUMS.txt` 用于核对下载文件。

需要先安装官方 Codex Windows 应用。程序会从 `CODEX_CLI`、`PATH` 或 `%LOCALAPPDATA%\OpenAI\Codex\bin\*\codex.exe` 定位 CLI。

## 使用

启动 Codex Switch 后会打开主面板，可以：

- 查看和切换 OpenAI / Sub2API Provider。
- 登录、切换、重命名或删除 OpenAI 账号。“登录新账号”使用后台浏览器 OAuth；若浏览器回调不便使用，可选择“设备码登录”。
- 查看每个账号的短期/周期剩余百分比、重置时间和重置卡。接口的 `usedPercent` 为已用量，界面显示 `100 - usedPercent`。
- 手动刷新额度、打开 Codex、设置开机启动或查看日志。
- 查看累计与本月 Token、账号去重合计，以及精确数量和每日记录的详情。

Provider 或账号切换会重启 Codex，确认框会提前提示结束正在运行的任务。账号校验通过后，程序只请求 Codex 正常退出；未能完整退出时取消切换。Sub2API 必须已配置在 `config.toml`，且本机 `127.0.0.1:8080` 在线；Windows 版不会自动启动 Sub2API 服务。

## 1.0.4：窄长条与高 DPI 文字布局

- 默认窗口进一步收窄为 420 × 1040 逻辑像素，受工作区大小限制；继续保持单列账号列表。
- 修复构造期间自动缩放过早完成的问题：先创建完整控件树，再执行首次 DPI 缩放。
- 累计/本月汇总按实际字体测量高度，标题、数字、覆盖说明分区显示；宽度不足时自动纵排。
- 连接区、标题和底部操作区使用自适应行高，避免显示缩放下的裁字和重叠。
- 窄账号卡片保持累计/本月 Token 数字单行，超长数值省略显示并保留精确 Tooltip。

## 1.0.3：单列竖版与刷新稳定性

- 默认窄窗口单列竖版（540 × 920 逻辑像素，按工作区缩小）；账号数量增加时只增加滚动内容，不展开多列。
- 复用现有账号卡片。刷新完成后仅更新对应数据，不清空并重建整个列表。
- 后台刷新开始时保留可见数值，状态栏提示正在刷新；保留用户窗口位置、尺寸、选中账号和滚动位置。
- 对没有变化的属性和提示文本跳过赋值与重绘，移除绘制过程中的重绘触发，并为布局容器开启双缓冲。

## 1.0.2：Token 用量与界面

- 参照 [GitHub macOS 2.3.1](https://github.com/ld017/codex-switch) 的统计方案，用官方 `account/usage/read` 返回的 `summary.lifetimeTokens` 显示累计 Token。
- 本月按本机当前自然月汇总有效 `yyyy-MM-dd` 日期记录，同日重复取较大值，排除非法、负数与未来记录。只有服务返回的记录才纳入；没有记录显示“未获取”，不视为零。
- 卡片和合计统一用“亿 Token”并保留两位小数；小于显示精度的正数显示 `<0.01 亿`。详情保留精确值，日记录用“百万 Token”。
- 合计按保存的账号身份去重，忽略邮箱推测；未知身份不计入，并分别显示累计/本月覆盖数与缓存项数。
- Token 与额度独立处理：Token 不可用可保留其旧值和读取时间；不把 Token 接口失败当成额度失败。
- 主面板统一浅色圆角卡片、细额度条、账号状态徽标和主要按钮；1.0.2 曾使用双列，1.0.3 已按使用反馈改回单列竖版。
- “Token 详情”提供服务读取时间、精确数量、每日记录和独立错误原因。Token 是官方 Codex 活动统计，可能延迟，不代表普通 ChatGPT 聊天、Sub2API 用量或完整计费账单。

## 1.0.1 修复

- 每次启动 CLI 时重新定位安装路径，支持 Codex 更新后删除旧版本目录的情况。
- 每分钟自动刷新，最多同时刷新 3 个账号，逐账号更新；单账号总超时为 20 秒，切换操作会取消并等待后台刷新完成。
- 启动先显示缓存。刷新失败时显示缓存日期、具体错误提示和实际成功账号数，避免将失败显示为“刚刚更新”。
- 优先使用官方 `rateLimitsByLimitId.codex` 额度桶，缺失值显示未知，过去的重置时间显示“待刷新”。
- 查询当前账号时只使用实时 access token，通过 app-server 的外部令牌模式查询，令牌续期交给正在运行的 Codex；其他账号在隔离目录刷新，轮换后的凭据即使查询失败也会保存。
- 根据 Codex 应用包身份定位桌面进程，等待包括后端在内的进程树退出；切换前验证目标，保存原账号最新凭据，失败时恢复并核对。

额度字段依据：[OpenAI App Server 文档](https://learn.chatgpt.com/docs/app-server#6-rate-limits-chatgpt)。

每次登录都在独立临时 `CODEX_HOME` 中进行，并强制 `cli_auth_credentials_store = "file"`，避免 Windows Credential Manager 绕过账号隔离。登录成功并完成加密导入后，临时明文目录会被删除。

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
