#!/bin/zsh
set -euo pipefail
cd -- "${0:A:h}"
build_dir=$(mktemp -d "${TMPDIR:-/tmp}/codex-pace-build.XXXXXXXX")
trap 'rm -rf -- "$build_dir"' EXIT
app="$build_dir/Codex Pace.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp Info.plist "$app/Contents/Info.plist"
cp AppIcon.icns "$app/Contents/Resources/AppIcon.icns"
swiftc -O -target "$(uname -m)-apple-macosx14.0" \
    -module-cache-path "$build_dir/modules" \
    Advice.swift BudgetNotifications.swift BudgetWarning.swift CodexPace.swift Dashboard.swift Pacing.swift Telemetry.swift \
    -o "$app/Contents/MacOS/CodexPace"
/usr/bin/codesign --force --sign - --timestamp=none "$app"
if [[ -d 'Codex Pace.app' ]]; then
    mv -- 'Codex Pace.app' "$build_dir/previous.app"
fi
mv -- "$app" .
