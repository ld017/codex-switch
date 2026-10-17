# Codex Switch for Windows 1.0.7

Windows 10/11 x64，自带 .NET 8 运行时。发布标签 `windows-v1.0.7`。

## 刷新更及时

- 额度到达即更新，不等待可选 Token 统计；两类数据保留独立读取时间。
- 当前账号优先；手动刷新进行中再次点击，最多合并成一轮后续强刷。
- 登录、重登和切号完成后先更新本地界面，再后台刷新，慢账号不再拖住业务操作。
- 前台额度自动每分钟刷新，最小化每5分钟，恢复窗口时按过期情况补刷；Token自动每5分钟读取，手动刷新可强刷。

网络和服务统计自身仍可能延迟或超时；失败时保留最后已取得数据及原时间，不将缺失数据当作零。

## 内存与数据管理

- 查询并发上限从3降为2，保留短生命周期、隔离凭据和查询进程清理。
- stdout在增量读取过程中限制单条消息1,048,576字符；缓存读取上限8MiB。
- Token日明细去重后保留最近366个有效日期，服务累计总数不重算。
- 日志先脱敏，每条最多8KiB；每类日志最多当前文件及3个归档，各2MiB。旧超大日志仅读取有限尾部后裁剪。
- 启动后和每6小时在空闲阶段维护；明确可回收临时目录超过1小时重试清理，日志归档超过14天清理。
- 成功切号的可回收备份保留最近10份，超过30天可清理但保留最新一份；未知旧备份和恢复数据保留。

活动租约、pending和恢复标记保护临时凭据。登录目录保留至加密导入完成；隐藏或嵌套的未完成凭据写入不会因缺少最终auth.json而被清除。账号凭据与设置不按年龄自动删除。

## 验证

- 完整Windows测试：Core248项、Integration106项，共354项通过。
- CI补充验证了不同账号锁不因哈希分组碰撞而串行、等待者取消后锁可回收；进度与环境继承测试使用确定性协议信号和仓库内测试程序。
- 独立审查发现的中断凭据写入清理边界已补回归并修正。
- 5个合成账号连续100轮自动刷新：额度读取500次、Token读取5次、并发峰值2、每轮结束临时目录残留0。
- 合成WinForms界面渲染和布局检查通过；安装包/ZIP使用明确文件白名单，发布构建关闭调试符号并隐藏本机构建路径。

资源样本来自测试宿主，不能据此宣称已安装程序或整机的内存下降比例。本轮未对正在工作中的真实Codex执行切号或强制退出，也未覆盖本机已安装版本；程序尚未商业代码签名。

[总体架构](../architecture/windows-refresh-and-resources.md)、[实施计划](../architecture/windows-refresh-resource-plan.md)、[验证记录](../performance/2026-10-18/windows-verification.md)。

## 下载

- [安装包](https://github.com/ld017/codex-switch/releases/download/windows-v1.0.7/CodexSwitch-Setup-1.0.7-x64.exe)
- [免安装版](https://github.com/ld017/codex-switch/releases/download/windows-v1.0.7/CodexSwitch-1.0.7-win-x64.zip)
- [SHA256SUMS](https://github.com/ld017/codex-switch/releases/download/windows-v1.0.7/SHA256SUMS.txt)
