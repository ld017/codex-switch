# Windows 1.0.7 刷新、资源与存储实施计划

依据：[已确认总体架构](windows-refresh-and-resources.md)。用户于 2026-10-18 确认进入实施。本轮在 `codex/windows-refresh-resources` 工作，保留主目录未提交改动，基于 GitHub 最新 main。

## 实施顺序与责任边界

1. **刷新查询链。** 在 RPC 返回额度时立即发布，保持准确时间；Coordinator 当前账号优先、并发最多 2、Token 自动 300 秒复用/失败冷却；手动强刷绕过冷却。旧调用兼容，新 delegate 显式携带选项和进度。
   - 文件：`UsageRefreshCoordinator.cs`、`CodexRpcClient.cs`、`UsageFetchOptions.cs`、`UsageFetcher.cs`、`UsageSnapshot.cs`。
   - 验证：FakeCli 慢 Token、阶段额度可见、取消保留阶段额度、TTL/失败重试、active优先、并发上限、observer异常、原凭据保护回归。
2. **UI 调度。** 自动请求合并，手动请求最多排一轮；切号/登录成功先显示本地状态，退出业务锁后启动刷新；最小化300秒、前台60秒，恢复时过期补刷。
   - 文件：`MainForm.cs`、可测试的刷新策略/调度类、界面测试。
   - 验证：进行中连续手动请求仅增加一轮；取消清空pending；切号后新刷新、旧generation不回写；状态显示不把缓存误算为本轮成功。
3. **有限内存和缓存。** stdout读取过程限1,048,576字符；日明细有效日期去重取最近366天；累计总数不重新计算；cache最大8MiB且可重建；账号互斥采用固定数量锁。
   - 文件：`BoundedLineReader.cs`、`TokenUsageRetention.cs`、`UsageCache.cs`、`UsageFetcher.cs`、Token详情说明。
   - 验证：超长无换行/多行CRLF/EOF，重复日期/无效日期/当前月与累计不变；缓存损坏和超限不会读取超额数据或丢账号。
4. **数据生命周期。** 新临时目录有活动租约和pending状态，凭据落库后才ready；无法保存保留恢复资料；登录临时寿命覆盖加密导入。成功切号备份才有清理资格。
   - 文件：`TemporaryHomeLease.cs`、`TemporaryDirectoryCleaner.cs`、`LoginService.cs`、`AccountSwitchService.cs`、`UsageFetcher.cs`。
   - 验证：活动/恢复/未知目录保留；已ready过期目录清理；严格路径和reparse防护；凭据写入与恢复顺序不变；清理失败不覆盖原业务错误。
5. **定期维护和日志。** 日志各2MiB+3归档、单条8KiB、脱敏后限长；后台维护启动一次/6小时一次、只在无业务时运行；临时目录1小时、归档日志14天、成功备份最近10/30天且最新1份保留。
   - 文件：`DataMaintenanceService.cs`、`RotatingLogWriter.cs`、`CompositionRoot.cs`、`UiExceptionGuard.cs`、`MainForm.cs`。
   - 验证：隔离数据年龄/数量边界、文件锁/权限/reparse、取消与维护单飞；实际数据不作为测试清理对象。
6. **集成和发布。** 全量测试、隔离多轮资源测量和UI冒烟，更新版本1.0.7/文档；构建Setup、ZIP、SHA256，审查源代码与产物隐私，提交推送GitHub并核对release。
   - 验证：所有Core/Integration通过；新查询峰值≤2、完成/取消清理；记录本体内存与查询峰值限制；核对产物版本、ZIP白名单、校验值、远端commit/tag/资产。

## 执行方法

三个独立实现任务并行：刷新Core/RPC、UI调度、安全清理/存储。根代理负责Fetcher与Composition集成、缓存/明细边界、全分支审查及发布。测试使用任务屏障和假进程；不对正在承载工作任务的真实Codex执行切号。先写能失败的回归再修复，各自运行过滤测试后统一运行全量。

## 主要审查点

- 额度已发布后任何Token失败/取消不得恢复旧额度；旧统计时间不得变新。
- 登录/查询未知状态不能因年龄被删除；恢复资料不纳入强制配额。
- 活动租约、取消清理和切号事务不能重叠误删；maintenance失败不改变业务结果。
- 输入上界要在分配之前检查，cache和网络上界不影响账号库。
- 并发下降可能延长全账号完成，资源报告不承诺未经实测的内存降幅；发布物不含运行数据。
