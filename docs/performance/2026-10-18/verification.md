# Codex Switch 2.3.2 验收结果

验证日期：2026-10-18，macOS Apple Silicon。

## 功能验证

- XCTest实际执行141项，其中1项真实Sub2API预检按设定跳过，0失败；Swift Testing实际执行74项，0失败。
- 新增回归覆盖：重置卡明细null/缺省/空数组、慢Token前额度发布、Token请求跳过、部分响应和脱敏错误、旧缓存兼容、单并发、强刷合并、取消后防回写及立即重刷、大行和编码边界。
- null/缺省明细在旧实现下复现2项解码失败，修复后通过；取消后立即重刷先复现同时2个查询，修复后并发最大1。
- release构建、安装包签名完整性检查通过；本机多个账号的额度与Token完整结果均通过真实只读读取验证，查询结束后无残留租约。
- 此版本包含新版Codex CLI布局识别修复：兼容嵌套应用、bin启动器及旧路径，并核验所属应用的bundle identifier。

服务端可能延迟返回Token记录或发生超时。刷新成功不代表日统计已结算到今天；Token失败时有效额度与旧统计仍保留。

## 资源基准

仅测Swift共享代理：100个1 MiB合成JSON请求，相同优化编译和输入，before/after交错各3次，取中位数。

| 指标 | 修改前 | 修改后 |
| --- | ---: | ---: |
| 耗时 | 2.84秒 | 0.50秒 |
| 最大RSS | 100.7 MB | 12.6 MB |
| 峰值physical footprint | 117.5 MB | 8.1 MB |

16 MiB长行的两行扫描测试由17.91秒降至0.094秒，但该场景RSS从27.2 MB升至43.4 MB。不能将多请求收益推广到全部负载，亦不能将官方CLI内存计作Swift代理内存。

[完整基准及方法](proxy-benchmark.md)，[三次原始测量](proxy-benchmark-trials.json)。

## 验证边界

- 实际展开菜单的视觉布局、鼠标区域尚未完成现场核验；分阶段进度和发布逻辑已有自动回归。
- 已展开的Token详情不会在刷新途中重建；关闭后重新展开显示最新值。
- 本轮没有实际执行账号/provider切换，没有重启正在运行的Codex。代理更新对新进程生效。
- 不承诺整机或菜单程序常驻内存下降比例；合成负载结果仅属于所述Swift代理测试。

## 复验

标准macOS开发环境可执行：

```sh
swift test --enable-xctest --enable-swift-testing
swift build -c release
scripts/test-entrypoints.sh
```

确认输出包含实际测试数量，不能将 `No matching test cases were run` 视为通过。签名检查示例：

```sh
codesign --verify --deep --strict "$HOME/Applications/Codex Switch.app"
```

[总体架构与实施顺序](../../architecture/refresh-and-resources.md)。

## 发布隐私检查

公开文档不包含实际账号身份、逐账号刷新时间或本机备份记录。应用附件去除调试信息后重新签名；三个可执行文件的个人主目录路径匹配为零。源码、历史文本和截图完成常见个人标识与凭据模式的定向检查，未发现命中；这不构成对所有形式信息的零遗漏保证。

`scripts/package-macos-release.sh` 在应用和ZIP两个阶段运行 `scripts/check-release-privacy.py`，命中时仅报告相对文件、偏移和类别，不输出匹配原文。可独立复验：

```sh
python3 -m unittest discover -s scripts/tests -p 'test_release_privacy.py'
python3 scripts/check-release-privacy.py path/to/Codex-Switch-macOS-arm64.zip
```
