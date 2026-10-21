import AppKit
import CodexProfileCore
import SwiftUI

@MainActor
final class WarmupWindowController: NSWindowController, NSWindowDelegate {
    let viewModel: WarmupViewModel
    private var resourceTimer: Timer?

    init(viewModel: WarmupViewModel) {
        self.viewModel = viewModel
        let panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 620, height: 660),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: true)
        panel.title = "定时预热"
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.level = .normal
        panel.collectionBehavior = [.moveToActiveSpace]
        panel.contentMinSize = NSSize(width: 520, height: 450)
        panel.contentView = NSHostingView(rootView: WarmupView(model: viewModel))
        super.init(window: panel)
        panel.delegate = self
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func show() {
        guard let panel = window else { return }
        if !panel.isVisible { panel.center() }
        NSApp.activate(ignoringOtherApps: false)
        panel.makeKeyAndOrderFront(nil)
        viewModel.refreshClock()
        viewModel.resources = WarmupResources.snapshot()
        resourceTimer?.invalidate()
        let timer = Timer(timeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.viewModel.refreshClock()
                self?.viewModel.resources = WarmupResources.snapshot()
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        resourceTimer = timer
    }

    func closeWindow() { resourceTimer?.invalidate(); resourceTimer = nil; window?.orderOut(nil) }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        closeWindow()
        return false
    }
}

private struct WarmupView: View {
    @ObservedObject var model: WarmupViewModel
    @State private var tab = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text("定时预热").font(.title3.weight(.medium))
                    Text("提前启动 5 小时额度窗口").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                if model.isRunning { ProgressView().controlSize(.small) }
            }
            Text("当前北京时间：\(model.currentTimeText)")
                .font(.caption).monospacedDigit().foregroundStyle(.secondary)
            Picker("页面", selection: $tab) {
                Text("计划设置").tag(0)
                Text("执行结果").tag(1)
            }.pickerStyle(.segmented)
            if let error = model.controlLoadError {
                Label(error, systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.red).font(.callout).textSelection(.enabled)
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    if tab == 0 { planView } else { resultView }
                    resourceView
                }
            }
            if !model.lastMessage.isEmpty {
                Text(model.lastMessage).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            }
        }
        .padding(22)
        .environment(\.locale, Locale(identifier: "zh_CN"))
        .environment(\.timeZone, WarmupPlanner.calendar.timeZone)
        .environment(\.calendar, WarmupPlanner.calendar)
        .frame(minWidth: 520, minHeight: 450)
    }

    private var planView: some View {
        VStack(alignment: .leading, spacing: 16) {
            Toggle(isOn: $model.draft.enabled) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("启用计划").font(.headline)
                    Text(model.hasUnsavedChanges ? "修改尚未保存" : "当前显示已保存设置")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }.toggleStyle(.switch)
            accountPicker
            Picker("时间模式", selection: Binding(get: { model.draft.mode }, set: { model.setMode($0) })) {
                ForEach(WarmupTimeMode.allCases) { Text($0.title).tag($0) }
            }.pickerStyle(.segmented)
            DatePicker(model.draft.mode == .warmup ? "预热时刻" : "目标重置时刻（预计）",
                       selection: Binding(get: { model.timePickerDate }, set: { model.timePickerDate = $0 }),
                       displayedComponents: .hourAndMinute)
            Picker("重复（按目标重置日）", selection: $model.draft.repeatRule) {
                ForEach(WarmupRepeat.allCases) { Text($0.title).tag($0) }
            }
            if model.draft.repeatRule == .once {
                DatePicker("目标重置日期", selection: Binding(get: { model.targetPickerDate }, set: { model.targetPickerDate = $0 }), displayedComponents: .date)
            }
            HStack {
                Text("时区").foregroundStyle(.secondary)
                Spacer()
                Text("北京时间 · UTC+8")
            }.font(.caption)
            GroupBox("下一次计划预览") {
                Text(model.preview).font(.callout).frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 4)
            }
            Text("已有窗口跳过 · 错过不补发")
                .font(.caption).foregroundStyle(.secondary)
            Text("预热会发送一次最小请求并消耗额度；重置时刻以服务器报告及后续核验为准。")
                .font(.caption).foregroundStyle(.secondary)
            if let validation = model.validationMessage {
                Text(validation).font(.caption).foregroundStyle(.red)
            }
            HStack {
                Spacer()
                Button("立即预热") { model.runNow(); tab = 1 }.disabled(!model.canRun)
                Button("保存计划") { model.saveDraft() }
                    .buttonStyle(.borderedProminent)
                    .disabled(model.isRunning || model.validationMessage != nil || model.onSave == nil)
            }
            Text(savedStatus).font(.caption).foregroundStyle(.secondary)
        }
        .disabled(model.isRunning)
    }

    private var accountPicker: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("预热账号").font(.subheadline.weight(.medium))
                Spacer()
                Text("已选 \(model.draft.accounts.count)/4").font(.caption).foregroundStyle(.secondary)
            }
            if model.profiles.isEmpty {
                Text("暂无已保存账号，请先在菜单中添加 OpenAI 账号。")
                    .font(.callout).foregroundStyle(.secondary)
            }
            LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 8) {
                ForEach(model.profiles) { profile in
                    Toggle(isOn: Binding(
                        get: { model.draft.accounts.contains(profile.id) },
                        set: { model.setSelected(profile.id, selected: $0) })) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(profile.label).lineLimit(2)
                                if !profile.isAvailable { Text("需要重新登录").font(.caption).foregroundStyle(.secondary) }
                            }
                        }
                        .toggleStyle(.checkbox)
                        .disabled(!model.draft.accounts.contains(profile.id) && (!profile.isAvailable || model.draft.accounts.count >= 4))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(8)
                        .background(.quaternary, in: RoundedRectangle(cornerRadius: 7))
                }
            }
            let missing = model.draft.accounts.subtracting(Set(model.profiles.map(\.id)))
            if !missing.isEmpty {
                HStack {
                    Text("\(missing.count) 个所选账号已被移除").font(.caption).foregroundStyle(.red)
                    Button("移除无效选择") { model.draft.accounts.subtract(missing) }.font(.caption)
                }
            }
        }
    }

    private var resultView: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text(model.isRunning ? "正在执行或核验" : "最近执行结果").font(.headline)
                Spacer()
                Button("复查结果") { model.recheck() }
                    .disabled(model.isRunning || model.results.isEmpty || model.onRecheck == nil || model.controlLoadError != nil)
            }
            if model.results.isEmpty {
                ContentUnavailableView("暂无执行结果", systemImage: "clock.arrow.circlepath",
                                       description: Text("保存计划或立即预热后，在这里查看服务器确认结果。"))
            }
            ForEach(model.results) { result in
                VStack(alignment: .leading, spacing: 5) {
                    HStack {
                        Text(result.label).font(.body.weight(.medium))
                        Spacer()
                        Text(result.statusTitle).font(.callout)
                    }
                    if let reset = result.resetAt {
                        Text("服务器报告重置：\(WarmupViewModel.display(reset, includesSeconds: true))")
                            .font(.callout).monospacedDigit()
                    }
                    if !result.detail.isEmpty {
                        Text(result.detail).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                    }
                    Divider().padding(.top, 5)
                }
            }
            Text("待确认的请求只复查，不会重复发送。")
                .font(.caption).foregroundStyle(.secondary)
            if !model.maintenanceSummary.isEmpty {
                Text(model.maintenanceSummary).font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private var savedStatus: String {
        guard model.savedPlan?.enabled == true else { return "已保存计划未启用 · 关闭窗口会保留草稿" }
        let next = model.nextExecutionAt.map { "下一次：\(WarmupViewModel.display($0)) · " } ?? ""
        return next + "关闭窗口不会停止计划；退出 Codex Switch 后不再执行。"
    }

    private var resourceView: some View {
        GroupBox("资源与存储") {
            VStack(alignment: .leading, spacing: 6) {
                if let resources = model.resources {
                    Text("预热 CLI：\(resources.activeCLIs) 个 · 内存 \(memory(resources.cliBytes)) · 工具自身 \(memory(resources.applicationBytes))")
                    Text("本次启动已采样峰值：\(resources.peakCLIs) 个 CLI／\(memory(resources.peakCLIBytes))")
                }
                Text("窗口显示时每2秒读取本机内存；关闭窗口即停止刷新。")
                Text("临时会话 · 令牌仅驻留内存 · CLI 退出后删除临时文件")
                Text("结果保留30天／200批／4 MiB；日志2 MiB轮转，旧日志14天清理。")
            }.font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading)
        }.padding(.top, 12)
    }

    private func memory(_ bytes: UInt64?) -> String {
        bytes.map { ByteCountFormatter.string(fromByteCount: Int64(clamping: $0), countStyle: .memory) } ?? "无法读取"
    }
}
