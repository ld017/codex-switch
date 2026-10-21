import Foundation

/// A local display clock with no connection or account-refresh dependency.
/// Its minute timer exists only while the menu is visible.
@MainActor
final class VisibleTimeClock {
    private let now: () -> Date
    private let onTick: (Date) -> Void
    private var timer: Timer?
    private var visible = false

    init(now: @escaping () -> Date = Date.init, onTick: @escaping (Date) -> Void) {
        self.now = now
        self.onTick = onTick
    }

    func setVisible(_ value: Bool) {
        visible = value
        timer?.invalidate()
        timer = nil
        if visible { refreshAndSchedule() }
    }

    func clockDidChange() {
        guard visible else { return }
        refreshAndSchedule()
    }

    func stop() { setVisible(false) }

    deinit { timer?.invalidate() }

    private func refreshAndSchedule() {
        timer?.invalidate()
        let date = now()
        onTick(date)
        let nextMinute = Date(timeIntervalSince1970: (floor(date.timeIntervalSince1970 / 60) + 1) * 60)
        let timer = Timer(fire: nextMinute, interval: 60, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.visible else { return }
                self.onTick(self.now())
            }
        }
        timer.tolerance = 1
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }
}
