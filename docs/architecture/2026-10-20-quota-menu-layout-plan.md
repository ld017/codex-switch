# macOS 账号菜单宽度与重置时刻实施计划

实施记录：2.3.5/build 14 已实现、回归并安装；[验收与限制](../verification/2026-10-20-quota-menu-layout.md)。原生菜单现场视觉因 CUA 超时仍未验收，以下保留原计划作为实施依据。

> 以下是设计阶段制定的原实施计划。实施已完成；本机测试与安装和原生菜单现场视觉的验收状态分别记录，不把计划中的预期结果当作实测结果。

**Goal:** 账号区铺满原生菜单可用宽度，主额度行同排显示北京时间重置时刻及倒计时。

**Spec:** `docs/architecture/2026-10-20-quota-menu-layout-design.md`

**Architecture:** 共享布局策略、纯重置时间模型、现有SwiftUI/NSHostingView及VisibleTimeClock。无新增计时器、网络检查或依赖。

**Tech Stack:** Swift、AppKit、SwiftUI、macOS14+、现有XCTest/Swift Testing。

## Global Constraints

- 用户已确认“具体时刻+倒计时同排”；基础宽度340pt，不增加卡片/header行数，不隐藏现有Token或重置卡信息。
- 只显示服务端resetAt，使用BeijingTime和同一帧now；不以now+5小时制造结果，不把时间过去写成已重置/预热成功。
- root、连接/Token子菜单、host保持稳定。显示tick只重算缓存，不触发额度CLI、端口探测、网络检查或root重建。
- 不在tracking期间手动调整宽度，不建立menu.size与host.width的反馈循环。
- 保留切号、共享历史、预热取消/退出等待、防重发和已有计划及启用状态。Windows、代理、电源不改。
- 合成数据验收，不启用/改写真实计划，不发真实模型请求、不重发unknown；最初实施范围不含推送、PR或Release，后续上传按用户的新指令执行。

## 基线与工作树

产品基线 `3c2378cc354c6bc4e2b41fcf016975487809117f`（本机2.3.4/build12），包含预热0013c8c、安装隐私1c86390、紧凑连接菜单、北京时间修正。

交接给出包含设计/计划的完整commit。新任务先查自己的artifacts和Git状态；复用本任务附着且符合基线的工作树，或用原生create_worktree从交接commit创建独立实施工作树。不可接管设计聊天工作树，不从旧main/默认远端分支构建；命令显式指定自己的返回目录，保留用户修改。

只读记录保存计划schema/revision/plan的规范化指纹及启用状态，不输出账号或计划原文。安装前再核对计划、真实预热子进程及下次触发距离，避免为UI验收中断真实执行，保留忙碌保护。

## Task 1: 纯重置时间展示模型

**Files:** Create `Sources/CodexProviderSwitcher/QuotaResetPresentation.swift`、`Tests/CodexProviderSwitcherTests/QuotaResetPresentationTests.swift`；Modify `Sources/CodexProviderSwitcher/MenuViews.swift` 的窗口标签纯函数。

**Interfaces:**

- `enum QuotaResetPresentation`；`enum State: Equatable { case future, notReturned, awaitingRefresh }`。
- `struct Value: Equatable`：`text`、`compactText`、`help`、`accessibilityLabel: String`，`state: State`。
- `static func make(resetAt: Date?, now: Date, isCached: Bool) -> Value`。
- `static func isCachedQuota(_ status: ProfileStatus) -> Bool`：available为false；stale/reloginNeeded携带snapshot为true；不复用TokenUsagePresentation.isCached。
- `usageWindowLabel(durationMins:legacyLabel:)`保留签名；300分钟显示“5小时”，其他小时数也用“小时”，其余真实duration维持分/天/周；主窗口无duration时legacyLabel为“主窗口”。

- [ ] 先写失败测试`testReportedResetTimeAndCountdownUseSameBeijingInstant`：`now=2026-10-20T08:10:00Z`（北京16:10），`resetAt=2026-10-20T12:31:00Z`，主文字“20:31 重置 · 余4时21分”，紧凑文字“20:31 · 4时21分”。
- [ ] 覆盖北京次日、跨月/跨年、缓存、nil、等于/早于now、不足1分、now推进1分钟、新snapshot改变resetAt；按spec文案表断言，help/accessibility给完整年份/秒/北京时间，不出现已重置/已恢复/已启动。
- [ ] 覆盖available且Token单独失败不标quota缓存，stale/reloginNeeded保留缓存；300分钟、其他duration、缺失duration不误标五小时。
- [ ] Run `env -u RUN_AUTHORIZED_WARMUP_LIVE -u RUN_LIVE_SWITCH_PREFLIGHT TZ=America/Chicago swift test --filter QuotaResetPresentationTests`。Expected：实现前接口/规则缺失FAIL，实现后全部GREEN。
- [ ] 实现纯转换，复用BeijingTime及现有时间差算法；不持久化、改变Date或发请求。GREEN后提交该组件及测试。

## Task 2: 单一宽度策略及可伸缩host

**Files:** Create `Sources/CodexProviderSwitcher/AccountMenuLayout.swift`、`Tests/CodexProviderSwitcherTests/AccountMenuLayoutTests.swift`；Modify `MenuViews.swift`、`AccountManager.swift`、`MainMenuBuilder.swift`（均在Sources/CodexProviderSwitcher）。

**Interfaces:**

- `@MainActor enum AccountMenuLayout`：CGFloat静态值 `minimumContentWidth=340`、`horizontalPadding=10`、`windowLabelWidth=34`、`percentageWidth=30`、`primaryTimeWidth=144`、`rowSpacing=4`、`minimumBarWidth=100`。
- `static func makeHost<Content: View>(rootView: Content, height: CGFloat) -> NSHostingView<Content>`：初始宽340、指定高度、宽度autoresizing；不读取menu.size，不注册时钟或网络工作。

- [ ] 先写失败测试`testMenuHostsFillParentWithoutWidthDrift`：340/380/420pt父容器中生产header/card/divider host随父宽伸缩；同一host经历340→380→340回到原宽，不累计增宽。
- [ ] NSMenu和隔离账号fixture验证minimumWidth至少340、host共享规则、无首尾/相邻分隔线；不调用真实AppDelegate.applicationDidFinishLaunching或账号库。
- [ ] Run `env -u RUN_AUTHORIZED_WARMUP_LIVE -u RUN_LIVE_SWITCH_PREFLIGHT swift test --filter AccountMenuLayoutTests`。Expected：实现前FAIL，完成后宽度随动/稳定对象/布局GREEN。
- [ ] Header/card/divider和host统一采用策略，删除对应独立290pt锁定；SwiftUI在父内容宽度内铺满，Button/contentShape/hover背景完整覆盖，保留系统边距。
- [ ] root可设置minimumWidth340，但不是最大值；不修改其他窗口尺寸，不先缩短预热标题掩盖host问题。名称34、百分比30、时间144、间距4；340pt时进度条至少100。次窗口仍仅显示倒计时，但也在144pt时间列右对齐，不能保留另一处42pt宽度导致百分比错列。
- [ ] GREEN后提交布局组件、接线和测试。

## Task 3: 同排文字与稳定实时更新

**Files:** Modify `MenuViews.swift`、`AccountManager.swift`；Create `Tests/CodexProviderSwitcherTests/QuotaRowLayoutTests.swift`；必要时扩展 `Tests/CodexProviderSwitcherTests/BeijingTimeTests.swift` 的既有clock fixture。

**Interfaces:**

- UsageRow原参数增加 `resetPresentation: QuotaResetPresentation.Value? = nil`；nil保留次窗口倒计时，有值则同排显示主/紧凑文字，统一help/accessibility。
- AccountManager.appendItems用同一个now构建该批header/卡片；refreshDisplayedTimes(now:)仍以同一now更新缓存视图。
- ProfileCardView使用主snapshot.primaryResetAt及isCachedQuota(status)，不得取重置卡到期或secondaryResetAt。

- [ ] 先写失败测试：主窗口具体时刻/倒计时可见，周窗口仍有倒计时；nil/缓存/到期切换不增卡片高度。
- [ ] 写`testPrimaryResetUsesServerFieldAndKeepsTrackingObjects`：fixture的primaryResetAt、secondaryResetAt、重置卡expiresAt设为互不相同的时刻；主窗口必须使用primary，不能取错字段或用now+5小时。
- [ ] 对spec全部主/紧凑文字用实际生产字体测量并离屏渲染；ViewThatFits候选按intrinsic文本判断，不把先压缩截断的Text误判为适配。关键时刻、日期、缓存标记不能省略；时间字号至少9pt。
- [ ] 0/1/4账号、长label、重复提示、不同plan、重置卡、relogin/失败fixture检查各列、hover背景、点击区域及底部可达；四账号高度不因新增文字增加。
- [ ] 对同一root/子菜单/host推进now跨分钟/北京午夜并更新snapshot.resetAt；断言文案变化、对象身份保持、fixture查询/操作计数不增长。
- [ ] Run `env -u RUN_AUTHORIZED_WARMUP_LIVE -u RUN_LIVE_SWITCH_PREFLIGHT TZ=America/Chicago swift test --filter 'QuotaResetPresentationTests|AccountMenuLayoutTests|QuotaRowLayoutTests|BeijingTimeTests|BeijingTokenMenuClockTests|VisibleTimeClockTests|CompactMenuIntegrationTests|AccountMutationTests|WarmupServiceTests'`。Expected：新接线前FAIL，完成后新规则及回归全部GREEN。
- [ ] 按spec接线及说明header时间口径；不增加采样，不改VisibleTimeClock生命周期，不将tick接到refreshStatus/账号刷新。GREEN后提交UI与测试。

## Task 4: 全量验证、安装和交付

**Files:** 必要时修改README.md；本机版本识别修改AppInfo.swift/Resources/Info.plist（实时核对当前版本，预计2.3.5/build13）；本地创建忽略的 `artifacts/quota-menu-layout/验收记录.md`。

- [ ] 对照spec自检并做独立只读代码审查：width反馈、tracking尺寸变化、缓存语义、字段、请求计数、北京时间及预热保留。
- [ ] Run `git diff --check`、`env -u RUN_AUTHORIZED_WARMUP_LIVE -u RUN_LIVE_SWITCH_PREFLIGHT swift test`、`bash -n scripts/build-and-install.sh`、`bash scripts/test-entrypoints.sh`、`python3 -m unittest discover -s scripts/tests -p 'test_release_privacy.py'`。Expected：exit0；记录真实通过/跳过数，不采用旧日志。
- [ ] 生产组件离屏合成fixture验340/380/420pt、浅/深色和全部状态；不把手画图当布局通过。额外示意/标注优先Inkscape。真实账号图不得提交/公开。
- [ ] 实时核对保存计划、实际预热/认证操作和下次触发距离；忙碌或临近计划时不为截图强行退出，不绕过正常退出保护。
- [ ] Run `env -u RUN_AUTHORIZED_WARMUP_LIVE -u RUN_LIVE_SWITCH_PREFLIGHT bash scripts/build-and-install.sh`。Expected：先测试/构建/prefix-map/strip/签名/隐私，再正常退出旧工具、备份、安装启动；保留北京时间备份名，不强制结束桌面Codex。
- [ ] 对安装文件fresh执行严格codesign校验与check-release-privacy.py，核对版本、运行进程、旧版备份及保存计划指纹/启用状态。
- [ ] 安装确认后尝试真实菜单：铺满、重置时刻、连续展开/分钟刷新、Token/预热/底部入口可达；不通过立即预热、保存计划、切provider验证UI。
- [ ] 若CUA仍timeoutReached/native pipe closed，标现场视觉未验收，不反复要求解锁/重开；合成、组件、用户观察分别记录。
- [ ] 提交产品改动，保留工作树并报告版本/commit/证据；未获发布指令不推送/发布Release。

## Review Focus 与自检映射

1. SwiftUI仍锁290、host不伸缩或反复增宽：Task2父宽往返测试及Task4视觉。
2. 文字截断、标签省略或四账号变高：Task1文案、Task3测量/fixture及Task4视觉。
3. 错误时间字段、缓存/过期误写已重置、Token失败污染quota缓存：Task1规则和Task3接线。
4. tick发请求、重建tracking菜单或北京跨日错误：Task3时钟/对象身份/查询计数。
5. 旧main安装丢失预热/北京时间、改写真实计划或绕过隐私：基线约束及Task4 fresh验证。

自检：Task1接口与Task3同名消费；Task2覆盖全部账号宽度；Task3不增加行高或后台工作；Task4区分合成/安装/现场结果。没有未定义选择、待填项或隐含发布动作。
