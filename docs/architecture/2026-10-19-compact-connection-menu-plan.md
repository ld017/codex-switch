# Codex Switch 紧凑连接菜单实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** 将主菜单顶部五项环境说明收为一个可展开的连接入口，使账号操作前移，并将诊断采样改为按需执行。

**Architecture:** 值类型快照和纯展示策略决定文案；一个控制器复用现有读取方法管理缓存与单任务；一个原生菜单组件持有稳定项。AppDelegate 只组合菜单和转发生命周期，账号及预热逻辑不变。

**Tech Stack:** Swift、AppKit NSMenu、Foundation、现有 XCTest/Swift Testing；macOS 14及以上，无新增依赖。

**Spec:** `docs/architecture/2026-10-19-compact-connection-menu-design.md`

## Global Constraints

- 仅 macOS 菜单，不改 Windows。
- 主菜单环境状态区始终恰好1项；异常也通过同一个父项表达，不插入另一条常驻提示。
- 账号区保持 AccountManager.appendItems 的内部顺序；定时预热完整接线必须保留。
- 两个提供方切换动作在主菜单账号区之后，选中和忙碌禁用逻辑保持原样。
- 共享刷新动作只迁入连接子菜单，保留原 selector、确认、恢复及禁用逻辑。
- 诊断缓存60秒，时间差小于0或大于等于60秒失效；一个采样任务、至多一个合并后的后续需求。
- 菜单关闭时不周期运行诊断子进程；用户操作 preflight 不使用展示缓存。
- 不改代理要求、连接策略、凭据、服务恢复、共享历史或预热发送；不发送真实模型请求来验证 UI。
- 安装必须沿用含前缀映射、strip、签名、隐私门禁和回滚备份的最新脚本。

## Review Focus

1. OpenAI 为当前配置时，备用 Sub2API 端口失败或代理未匹配不能被写成当前连接离线——Task 1覆盖。
2. 首次未检查、检查中、配置读取失败和过期缓存必须有不同语义，不能默认成功/离线——Task 1、2覆盖。
3. 提供方变化、服务恢复和慢异步旧结果不能造成旧告警回写；重复打开不能堆积采样——Task 2覆盖。
4. 展开子菜单不能刷新账号、重建整个主菜单或丢失正在跟踪的 Token/账号区域——Task 3覆盖。
5. 布局调整不能丢失切换、共享刷新、预热取消/退出等待和安装隐私门禁——Task 3、4覆盖。

## 基线与执行环境

用户已明确要求“计划→框架设计→实施计划→新开任务执行”。原任务完成设计和自检后负责新建执行任务，本轮不修改产品代码。

实施必须包含功能提交 `0013c8c` 和安装隐私修正 `1c86390`。原项目 main 仍为 `8d72e27`，直接从旧 main 构建会丢失预热。

新任务先检查自己的 artifacts 和 Git 状态；复用自己已附着且符合基线的工作树，或用原生 worktree API 从交接消息给出的文档提交创建该任务自己的实施工作树。显式指定 ref，不从远端默认分支盲目开始，不接管原任务的工作树，不重置用户修改。之后所有命令明确指定返回的 workspace 目录。

## Task 1: 定义快照和纯展示规则

**Files:**
- Create: `Sources/CodexProviderSwitcher/ConnectionStatusPresentation.swift`
- Create: `Tests/CodexProviderSwitcherTests/ConnectionStatusPresentationTests.swift`

**Interfaces:**
- `enum MenuProviderState: Equatable { case checking, configured(Provider), unreadable }`
- `struct ConnectionStatusSnapshot: Equatable`，字段：`provider`；`sub2APIOnline: Bool?`；`sub2APICheckedAt: Date?`；`proxyConflictVariables: [String]?`；`clashReadiness: ClashReadinessStatus?`；`sharedHistoryConfigured: Bool?`；`diagnosticsCheckedAt: Date?`；`isRefreshingDiagnostics: Bool`；`isRefreshingActiveChannel: Bool`。默认未知，`static let cacheLifetime: TimeInterval = 60`。
- `struct ConnectionStatusPresentation: Equatable`，字段 `title: String`、`emphasis: Emphasis`（`.normal/.attention`）、`details: [DetailRow]`。
- `DetailRow` 有固定 `id` 和 `title`；ID 为 provider、sub2API、proxyEnvironment、proxyReadiness、sharedHistory、lastChecked。
- `static func make(snapshot: ConnectionStatusSnapshot, now: Date) -> ConnectionStatusPresentation`。

- [ ] 写失败测试，覆盖设计第5、6节的文案表；重点断言 OpenAI+备用端口失败仍为“连接：OpenAI 官方”且无 attention，当前 Sub2API 新鲜失败为“Sub2API · 端口检查未通过”，配置无法读取优先。
- [ ] 测试端口样本在59秒时有效、60秒时过期、时间回拨时过期；过期失败不维持顶部告警。检查中不输出成功或离线。
- [ ] Run: `swift test --filter ConnectionStatusPresentationTests`，确认新增规则在实现前失败。
- [ ] 实现上面的值类型与纯转换；详情只保留变量名/数量，不保留或显示变量值、凭据或代理认证 URL。未知/旧缓存语义按 spec。
- [ ] 重跑 scoped tests，通过后提交此组件。

## Task 2: 按需诊断控制器

**Files:**
- Create: `Sources/CodexProviderSwitcher/ConnectionStatusController.swift`
- Create: `Tests/CodexProviderSwitcherTests/ConnectionStatusControllerTests.swift`
- Modify: `Sources/CodexProviderSwitcher/CodexSystem.swift`（仅协议适配，不改检查语义）

**Interfaces:**
- `@MainActor protocol ConnectionStatusSource: AnyObject`，复用当前签名：`currentProvider() throws -> Provider`、`sub2APIIsOnline() async -> Bool`、`proxyEnvironmentConflicts() async -> [ProxyEnvironmentConflict]`、`clashReadinessStatus() async -> ClashReadinessStatus`、`sharedHistoryIsConfigured() async -> Bool`。
- `CodexSystem` 适配此协议；不另建 shell/network 探测实现。
- `@MainActor final class ConnectionStatusController`：`init(source: any ConnectionStatusSource, now: @escaping () -> Date = Date.init)`；`private(set) var snapshot: ConnectionStatusSnapshot`；`var onChange: ((ConnectionStatusSnapshot) -> Void)?`。
- 方法：`refreshProvider()`（同步，只读配置）、`requestActiveChannelStatus()`、`requestDiagnostics()`（发起受控异步任务）、`invalidate()`（废弃旧周期/缓存）、`stop()`（取消并禁止旧结果回写）。

- [ ] 用可控时钟、计数 source 和异步 latch 写失败测试：OpenAI root 刷新不触发诊断子进程；Sub2API 只请求端口；完整诊断包含各来源；60秒内重复请求复用缓存。
- [ ] 覆盖并发合并：重复展开只保留一个任务；端口采样期间完整诊断需求最多追加一次完整采样。
- [ ] 覆盖 provider 变化、invalidate/stop 后迟到结果不回写，恢复服务后的旧失败被废弃，时钟回拨重新请求。增加 source 忽略取消且迟到返回的 latch 测试，断言实际采样批次最大并发仍为1；取消后仍占用采样槽，等旧任务及实际 source 工作退出后才清槽并启动合并请求，stop 清除后续需求。异步结果只更新诊断字段，不能覆盖新的 provider。端口时间戳取端口检查完成时刻，不能等整批结束才把旧端口结果标成新鲜。
- [ ] Run: `swift test --filter ConnectionStatusControllerTests`，确认 RED；实现有界快照、60秒缓存和 generation/cancellation。将冲突信息归一成变量名，不保留值。
- [ ] Scoped tests GREEN 后提交。不要新增计时器或持久化状态。

## Task 3: 连接菜单组件与主菜单接线

**Files:**
- Create: `Sources/CodexProviderSwitcher/ConnectionMenuSection.swift`
- Create: `Sources/CodexProviderSwitcher/MainMenuBuilder.swift`
- Create: `Tests/CodexProviderSwitcherTests/ConnectionMenuSectionTests.swift`
- Create: `Tests/CodexProviderSwitcherTests/CompactMenuIntegrationTests.swift`
- Modify: `Sources/CodexProviderSwitcher/AppDelegate.swift`

**Interfaces:**
- `@MainActor final class ConnectionMenuSection: NSObject, NSMenuDelegate`。
- `init(refreshSharedHistoryItem: NSMenuItem, onOpen: @escaping () -> Void)`；`let item: NSMenuItem`；`func update(_ presentation: ConnectionStatusPresentation)`。
- 一个父项、一个子菜单和固定详情项一次性建立；只更新 title/image。父项使用 network 或 exclamationmark.triangle，始终可展开。
- `@MainActor enum MainMenuBuilder` 提供 `static func populate(menu: NSMenu, connectionItem: NSMenuItem, appendAccounts: (NSMenu) -> Void, providerItems: [NSMenuItem], trailingItems: [NSMenuItem])`，只按 spec 组合菜单；它不初始化账号库或发起检查。
- 子菜单独立处理自己的 menuWillOpen；AppDelegate 保留自己的 root delegate，且 `menuWillOpen/menuDidClose` 增加 root 身份判断。

- [ ] 用 MainMenuBuilder 和隔离账号 fixture 写失败测试，不调用 AppDelegate.applicationDidFinishLaunching 或真实账号库：root 状态区恰好一个父项；旧五行不在 root；账号区在提供方操作之前；两个提供方动作各一次；共享刷新只在子菜单一次；定时预热入口仍恰好一次；空账号 fixture 不出现相邻或首尾分隔线。
- [ ] 测试连续 update 保持父项/子项对象身份，不重建菜单；子菜单打开只调用诊断 onOpen，不触发账号刷新或 AppDelegate root 重建。
- [ ] 测试切换忙碌时父项可查看，修改类动作仍禁用；失败/恢复只改变同一个父项，不插入新行。
- [ ] 在 AppDelegate 中用新 section/controller 替换旧 currentItem/serviceItem/proxyEnvironmentItem/sharedHistoryStatusItem 与状态字符串拼装，删除旧独立标题行。由 MainMenuBuilder 按 spec 第4节重排主菜单。refreshStatus 通过 controller.refreshProvider 读取一次 provider，再从同一 snapshot 更新标题和提供方动作状态，避免重复读取造成不一致。
- [ ] 保留原60秒 timer，但把重诊断从通用 refreshStatus 移出；root 打开/保持打开且当前 Sub2API 时才请求 active status，子菜单打开才请求完整 diagnostics。异步 onChange 仅更新 section，不调用 rebuildMenu。
- [ ] 在 provider 切换结束后 invalidate，再刷新 provider。退出时 stop 控制器；不删除 WarmupService.start/stop/shutdown、warmupTitle/onChange、账号变更前取消、provider 切换前取消等接线。
- [ ] Run: `swift test --filter 'ConnectionMenuSectionTests|CompactMenuIntegrationTests|AccountMutationTests|WarmupViewModelTests|WarmupServiceTests'`；GREEN 后提交。

## Task 4: 全量复核、界面验收与安装

**Files:**
- Modify: `docs/warmup.md`（只在入口描述需要适配时）
- Create: `artifacts/compact-connection-menu/验收记录.md`（本地忽略，不提交个人数据）

- [ ] 对照 spec 与本计划逐项自检，并做独立审查；尤其检查检查语义、菜单生命周期、迟到结果和预热功能保留。修复阻断问题后再进入安装。
- [ ] Run: `git diff --check`、`swift test`、`bash -n scripts/build-and-install.sh`、`bash scripts/test-entrypoints.sh`、`python3 -m unittest discover -s scripts/tests -p 'test_release_privacy.py'`。记录实际通过/跳过数，禁止凭旧日志宣称通过。
- [ ] Run: `bash scripts/build-and-install.sh`。必须保留忙碌退出保护、旧版备份、前缀映射/strip/签名及退出旧版前的隐私扫描。旧版不能正常退出时不强制杀掉桌面 Codex。
- [ ] 安装后 fresh 执行严格 codesign 校验和 `python3 scripts/check-release-privacy.py '<installed app>'`；确认实际进程来自新安装、预热计划未被改动、旧版备份存在。
- [ ] 在确认新版本已安装并运行后，检查真实菜单：状态区只有一行、账号卡前移、诊断展开与缓存文案、反复展开不关闭菜单、不影响 Token/预热入口。仅开关/展示检查，不点模型请求，不为截图强制切换/重启正在工作的桌面 Codex。
- [ ] 若 CUA 仍出现 native pipe closed，记录为未完成视觉验收；组件测试、用户确认和工具观察分别标注，不反复要求用户执行同一步。
- [ ] 更新验收记录，区分代码/测试、安装、现场视觉结果，提交产品改动并保留可追溯工作树。未获新指令不推送 GitHub 或创建公开发布。

## 计划自检

- 展示压缩、诊断保留、账号前移：Task 1、3。
- 源码证据与文案语义边界：Task 1。
- 按需刷新、缓存、并发和迟到结果：Task 2。
- 子菜单生命周期、稳定对象与动作可用性：Task 3。
- 预热、连接检查、共享恢复、隐私与回滚不回退：Task 3、4。
- 执行基线明确；没有依赖旧 main 的步骤，没有要求额外真实预热，没有待定项或新增用户授权门槛。

附加审查修正：补明取消后等待实际 source 退出的单任务约束；将真实新菜单视觉验收排到安装和版本确认之后，避免误验旧版。
