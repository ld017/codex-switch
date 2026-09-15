#!/bin/bash
set -euo pipefail

SCRIPT_DIRECTORY="$(cd "$(dirname "$0")" && pwd -P)"
PROJECT_DIRECTORY="$(cd "${SCRIPT_DIRECTORY}/.." && pwd -P)"
USER_HOME="$(/usr/bin/dscl . -read "/Users/$(/usr/bin/id -un)" NFSHomeDirectory | /usr/bin/awk '{print $2}')"
APPLICATIONS_DIRECTORY="${USER_HOME}/Applications"
FINAL_APPLICATION="${APPLICATIONS_DIRECTORY}/Codex Switch.app"
LEGACY_APPLICATION="${APPLICATIONS_DIRECTORY}/Codex Provider Switcher.app"
FINAL_EXECUTABLE="${FINAL_APPLICATION}/Contents/MacOS/CodexProviderSwitcher"
FINAL_PROXY="${FINAL_APPLICATION}/Contents/MacOS/CodexSharedHistoryProxy"
FINAL_PROFILE_HELPER="${FINAL_APPLICATION}/Contents/Helpers/codex-profile"
SWITCHER_DATA_DIRECTORY="${USER_HOME}/.codex/provider-switcher"
STAGING_DIRECTORY="$(/usr/bin/mktemp -d "${PROJECT_DIRECTORY}/.build/install.XXXXXX")"
STAGED_APPLICATION="${STAGING_DIRECTORY}/Codex Switch.app"
SIGNING_IDENTITY="${CODEX_SWITCH_SIGNING_IDENTITY:--}"
LAUNCH_SERVICES_REGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
INITIAL_INSTALL=false
if [[ ! -d "${FINAL_APPLICATION}" && ! -d "${LEGACY_APPLICATION}" ]]; then
    INITIAL_INSTALL=true
fi

cleanup() {
    /bin/rm -rf "${STAGING_DIRECTORY}"
}
trap cleanup EXIT

cd "${PROJECT_DIRECTORY}"
/usr/bin/swift test
/usr/bin/swift build -c release

/bin/mkdir -p \
    "${STAGED_APPLICATION}/Contents/MacOS" \
    "${STAGED_APPLICATION}/Contents/Helpers" \
    "${STAGED_APPLICATION}/Contents/Resources" \
    "${APPLICATIONS_DIRECTORY}" \
    "${SWITCHER_DATA_DIRECTORY}"

/usr/bin/ditto \
    "${PROJECT_DIRECTORY}/.build/release/CodexProviderSwitcher" \
    "${STAGED_APPLICATION}/Contents/MacOS/CodexProviderSwitcher"
/usr/bin/ditto \
    "${PROJECT_DIRECTORY}/.build/release/CodexSharedHistoryProxy" \
    "${STAGED_APPLICATION}/Contents/MacOS/CodexSharedHistoryProxy"
/usr/bin/ditto \
    "${PROJECT_DIRECTORY}/.build/release/codex-profile" \
    "${STAGED_APPLICATION}/Contents/Helpers/codex-profile"
/usr/bin/ditto \
    "${PROJECT_DIRECTORY}/Resources/Info.plist" \
    "${STAGED_APPLICATION}/Contents/Info.plist"
/usr/bin/ditto \
    "${PROJECT_DIRECTORY}/Resources/AppIcon.icns" \
    "${STAGED_APPLICATION}/Contents/Resources/AppIcon.icns"
/bin/chmod 755 \
    "${STAGED_APPLICATION}/Contents/MacOS/CodexProviderSwitcher" \
    "${STAGED_APPLICATION}/Contents/MacOS/CodexSharedHistoryProxy" \
    "${STAGED_APPLICATION}/Contents/Helpers/codex-profile"
/usr/bin/plutil -lint "${STAGED_APPLICATION}/Contents/Info.plist"

/usr/bin/codesign --force --sign "${SIGNING_IDENTITY}" \
    "${STAGED_APPLICATION}/Contents/MacOS/CodexProviderSwitcher"
/usr/bin/codesign --force --sign "${SIGNING_IDENTITY}" \
    "${STAGED_APPLICATION}/Contents/Helpers/codex-profile"
/usr/bin/codesign --force --sign "${SIGNING_IDENTITY}" \
    "${STAGED_APPLICATION}/Contents/MacOS/CodexSharedHistoryProxy"
/usr/bin/codesign --force --sign "${SIGNING_IDENTITY}" \
    "${STAGED_APPLICATION}"
/usr/bin/codesign --verify --deep --strict "${STAGED_APPLICATION}"

if /usr/bin/pgrep -f "^${FINAL_EXECUTABLE}$|^${LEGACY_APPLICATION}/Contents/MacOS/CodexProviderSwitcher$" \
    >/dev/null 2>&1; then
    /usr/bin/osascript \
        -e 'tell application id "com.lindui017.codex-provider-switcher" to quit' \
        >/dev/null 2>&1 || true
    for _ in {1..40}; do
        if ! /usr/bin/pgrep -f "^${FINAL_EXECUTABLE}$|^${LEGACY_APPLICATION}/Contents/MacOS/CodexProviderSwitcher$" \
            >/dev/null 2>&1; then
            break
        fi
        /bin/sleep 0.25
    done
    if /usr/bin/pgrep -f "^${FINAL_EXECUTABLE}$|^${LEGACY_APPLICATION}/Contents/MacOS/CodexProviderSwitcher$" \
        >/dev/null 2>&1; then
        echo "Switcher is busy; installation was cancelled safely." >&2
        exit 1
    fi
fi

APP_BACKUP_DIRECTORY="${SWITCHER_DATA_DIRECTORY}/app-backups"
/bin/mkdir -p "${APP_BACKUP_DIRECTORY}"

if [[ -d "${LEGACY_APPLICATION}" ]]; then
    "${LAUNCH_SERVICES_REGISTER}" -u "${LEGACY_APPLICATION}" || true
    /bin/mv \
        "${LEGACY_APPLICATION}" \
        "${APP_BACKUP_DIRECTORY}/Codex Provider Switcher-$(/bin/date +%Y%m%d-%H%M%S).app.backup"
fi

if [[ -d "${FINAL_APPLICATION}" ]]; then
    "${LAUNCH_SERVICES_REGISTER}" -u "${FINAL_APPLICATION}" || true
    /bin/mv \
        "${FINAL_APPLICATION}" \
        "${APP_BACKUP_DIRECTORY}/Codex Switch-$(/bin/date +%Y%m%d-%H%M%S).app.backup"
fi

/usr/bin/ditto "${STAGED_APPLICATION}" "${FINAL_APPLICATION}"

"${LAUNCH_SERVICES_REGISTER}" -f "${FINAL_APPLICATION}"
if [[ "${INITIAL_INSTALL}" == true ]]; then
    /usr/bin/open -g "${FINAL_APPLICATION}" --args --enable-login-item
else
    /usr/bin/open -g "${FINAL_APPLICATION}"
fi

for _ in {1..40}; do
    if /usr/bin/pgrep -f "^${FINAL_EXECUTABLE}$" >/dev/null 2>&1; then
        break
    fi
    /bin/sleep 0.25
done

if ! /usr/bin/pgrep -f "^${FINAL_EXECUTABLE}$" >/dev/null 2>&1; then
    echo "Codex Switch did not start." >&2
    exit 1
fi

/bin/test -x "${FINAL_PROXY}"
/bin/test -x "${FINAL_PROFILE_HELPER}"
echo "Installed: ${FINAL_APPLICATION}"
