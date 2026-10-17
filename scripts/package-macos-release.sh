#!/bin/bash
set -euo pipefail

# Build release artifacts without embedding the publisher's home-directory paths.
# CODEX_SWITCH_SWIFT may name a Swift wrapper for a custom local toolchain.
SCRIPT_DIRECTORY="$(cd "$(dirname "$0")" && pwd -P)"
PROJECT_DIRECTORY="$(cd "${SCRIPT_DIRECTORY}/.." && pwd -P)"
PUBLISHER_HOME="${HOME:?HOME must be set}"
SWIFT_COMMAND="${CODEX_SWITCH_SWIFT:-/usr/bin/swift}"
BUILD_DIRECTORY="${CODEX_SWITCH_RELEASE_BUILD_DIR:-${PROJECT_DIRECTORY}/.build/private-release}"
SIGNING_IDENTITY="${CODEX_SWITCH_SIGNING_IDENTITY:--}"
VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "${PROJECT_DIRECTORY}/Resources/Info.plist")"
OUTPUT_DIRECTORY="${PROJECT_DIRECTORY}/.build/releases/${VERSION}"

if [[ ${1-} == --skip-build && $# == 1 ]]; then
    : # The privacy audit below still applies to prebuilt binaries.
elif [[ $# == 0 ]]; then
    "${SWIFT_COMMAND}" build -c release --package-path "${PROJECT_DIRECTORY}" \
        --scratch-path "${BUILD_DIRECTORY}" \
        -Xswiftc -debug-prefix-map -Xswiftc "${PUBLISHER_HOME}=/build/user" \
        -Xswiftc -file-prefix-map -Xswiftc "${PUBLISHER_HOME}=/build/user" \
        -Xswiftc -file-compilation-dir -Xswiftc /build/codex-switch \
        -Xcc "-fdebug-prefix-map=${PUBLISHER_HOME}=/build/user"
else
    echo 'Usage: scripts/package-macos-release.sh [--skip-build]' >&2
    exit 2
fi

mkdir -p "${OUTPUT_DIRECTORY}"
STAGING_DIRECTORY="$(mktemp -d "${PROJECT_DIRECTORY}/.build/release-package.XXXXXX")"
trap 'rm -rf "${STAGING_DIRECTORY}"' EXIT
APPLICATION="${STAGING_DIRECTORY}/Codex Switch.app"
mkdir -p "${APPLICATION}/Contents/MacOS" "${APPLICATION}/Contents/Helpers" \
    "${APPLICATION}/Contents/Resources"

for executable in CodexProviderSwitcher CodexSharedHistoryProxy; do
    cp "${BUILD_DIRECTORY}/release/${executable}" "${APPLICATION}/Contents/MacOS/${executable}"
done
cp "${BUILD_DIRECTORY}/release/codex-profile" "${APPLICATION}/Contents/Helpers/codex-profile"
cp "${PROJECT_DIRECTORY}/Resources/Info.plist" "${APPLICATION}/Contents/Info.plist"
cp "${PROJECT_DIRECTORY}/Resources/AppIcon.icns" "${APPLICATION}/Contents/Resources/AppIcon.icns"
chmod 755 "${APPLICATION}/Contents/MacOS/"* "${APPLICATION}/Contents/Helpers/codex-profile"

for executable in \
    "${APPLICATION}/Contents/MacOS/CodexProviderSwitcher" \
    "${APPLICATION}/Contents/MacOS/CodexSharedHistoryProxy" \
    "${APPLICATION}/Contents/Helpers/codex-profile"; do
    /usr/bin/strip -S "${executable}"
    /usr/bin/codesign --force --sign "${SIGNING_IDENTITY}" "${executable}"
done
/usr/bin/codesign --force --sign "${SIGNING_IDENTITY}" "${APPLICATION}"
/usr/bin/codesign --verify --deep --strict "${APPLICATION}"
python3 "${SCRIPT_DIRECTORY}/check-release-privacy.py" "${APPLICATION}"

ARCHITECTURE="$(/usr/bin/lipo -archs "${APPLICATION}/Contents/MacOS/CodexProviderSwitcher")"
if [[ ${ARCHITECTURE} != arm64 ]]; then
    echo 'This release target requires an arm64-only build.' >&2
    exit 1
fi
ARCHIVE_NAME="Codex-Switch-${VERSION}-macOS-arm64.zip"
ARCHIVE="${STAGING_DIRECTORY}/${ARCHIVE_NAME}"
/usr/bin/ditto -c -k --norsrc --noextattr --keepParent "${APPLICATION}" "${ARCHIVE}"
python3 "${SCRIPT_DIRECTORY}/check-release-privacy.py" "${ARCHIVE}"
(
    cd "${STAGING_DIRECTORY}"
    /usr/bin/shasum -a 256 "${ARCHIVE_NAME}" > SHA256SUMS.txt
)
mv "${ARCHIVE}" "${OUTPUT_DIRECTORY}/${ARCHIVE_NAME}"
mv "${STAGING_DIRECTORY}/SHA256SUMS.txt" "${OUTPUT_DIRECTORY}/SHA256SUMS.txt"
printf 'Release artifacts: %s\n' "${OUTPUT_DIRECTORY}"
