public enum SwitchDecision {
    public static func requiresSwitch(
        from current: Provider,
        to target: Provider
    ) -> Bool {
        current != target
    }
}
