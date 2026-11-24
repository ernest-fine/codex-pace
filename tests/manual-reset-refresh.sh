#!/bin/zsh
set -euo pipefail
cd -- "${0:A:h}/.."

scratch=$(mktemp -d "${TMPDIR:-/tmp}/pace-reset-test.XXXXXXXX")
test_bundle="local.codex-pace.test-$(uuidgen)"
cleanup() {
    defaults delete "$test_bundle" >/dev/null 2>&1 || true
    rm -f -- "$HOME/Library/Preferences/$test_bundle.plist"
    rm -rf -- "$scratch"
}
trap cleanup EXIT
app="$scratch/ResetTest.app/Contents"
profile="$scratch/profile"
mkdir -p "$app/MacOS" "$profile/.codex/archived_sessions" "$profile/Library/Preferences"
/usr/bin/plutil -create xml1 "$app/Info.plist"
/usr/bin/plutil -insert CFBundleIdentifier -string "$test_bundle" "$app/Info.plist"
/usr/bin/plutil -insert CFBundleExecutable -string ResetTest "$app/Info.plist"
/usr/bin/plutil -insert CFBundlePackageType -string APPL "$app/Info.plist"

swiftc -parse-as-library -emit-library -emit-module -enable-testing -module-name CodexPace \
    -module-cache-path "$scratch/modules" -emit-module-path "$scratch/CodexPace.swiftmodule" \
    *.swift -o "$scratch/libCodexPace.dylib"
swiftc -parse-as-library -I "$scratch" -L "$scratch" -lCodexPace -module-cache-path "$scratch/modules" \
    tests/ManualResetRefreshTests.swift -o "$app/MacOS/ResetTest"

start=$(( $(date +%s) - 60 ))
for phase in save relaunch after-clear; do
    env -u CODEX_HOME CFFIXED_USER_HOME="$profile" CODEX_PACE_CODEX=/usr/bin/true \
        "$app/MacOS/ResetTest" "$phase" "$profile" "$start"
done
echo "Manual reset target survived quota refresh and relaunch."
