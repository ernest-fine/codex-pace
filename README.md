# Codex Pace

Codex Pace is a desktop companion for tracking your Codex weekly quota. It compares actual usage with the pace you planned, shows the allowance left for the work ahead, and suggests a model and effort when you’re at risk of reaching a budget.

## Run Codex Pace

Sign in to Codex on the computer where you’ll run the tracker.

| Platform | Download | How to run |
| --- | --- | --- |
| macOS 14 or later on Apple silicon | [macOS ARM64](https://github.com/ernest-fine/codex-pace/releases/download/v1.22.0/Codex-Pace-macOS-arm64.zip) | Extract the ZIP and open **Codex Pace.app**. Session status appears in the menu bar. |
| Windows 11 on Intel or AMD | [Windows x64](https://github.com/ernest-fine/codex-pace/releases/download/v1.22.0/Codex-Pace-Windows-x64.zip) | Extract the ZIP and run `CodexPace.exe`. |
| Windows 11 on Arm | [Windows ARM64](https://github.com/ernest-fine/codex-pace/releases/download/v1.22.0/Codex-Pace-Windows-arm64.zip) | Extract the ZIP and run `CodexPace.exe`. |

The Windows packages include the .NET Desktop runtime. The macOS package uses an ad hoc signature. A trusted copy may require individual approval in Privacy & Security. [Apple’s first-launch guidance](https://support.apple.com/en-us/102445)

Keep Codex Pace running for background updates and notifications. Quota refreshes every two minutes. Use **Refresh** to check manually. See the [Windows guide](windows/README.md) for tray controls and setup details.

## Plan your quota

- **Session and weekly pace** show actual usage against the target by now. The header’s **Used / Left** switch changes both views and the Work sessions table.
- **Work days and named sessions** let you set recurring hours that match your schedule. Work sessions shows a row per workday and a column per session, with a cumulative quota target at each session’s end. Unused quota carries forward.
- **Forecasts and alerts** use your recent pace and scheduled time left to estimate when you might reach a session target or the weekly limit. Recent Codex activity helps the app suggest a model and effort for the next turn.
- **Manual reset targets** let you plan for an earlier deadline. Redeem the reset in Codex at the time you choose.

Schedules and reset times use your computer’s local time zone. Graphite follows the system’s light or dark appearance.

## How quota readings work

Codex Pace reads your signed-in account’s quota through Codex App Server. It can fall back to recent readings in the local Codex session history. The dashboard shows the source and age of the reading. Quota history, preferences, and your schedule are stored on your computer.

If Codex Pace needs help finding the CLI, set `CODEX_PACE_CODEX` to the full path of the native Codex executable.

## Build from source

**macOS** uses SwiftUI and the Xcode toolchain. Run this from the project root to create `Codex Pace.app`.

```sh
./build.sh
```

**Windows** uses WinForms and the .NET 10 SDK. From the project root, publish the x64 executable with its runtime bundled.

```sh
dotnet publish windows/CodexPace.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/windows-x64
```

For Windows on Arm, use `win-arm64` and output to `dist/windows-arm64`.
