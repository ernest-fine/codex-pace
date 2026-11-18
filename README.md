# Codex Pace

Codex Pace shows how much of your weekly Codex quota you’ve used and how much is available for each work session. It compares your usage with your plan and suggests a model, effort, and speed for your next turn.

## Run Codex Pace

Sign in to Codex on the computer where you’ll run the tracker.

| Platform | Download | How to run |
| --- | --- | --- |
| macOS 14 or later on Apple silicon | [macOS 1.23.5](https://github.com/ernest-fine/codex-pace/releases/download/v1.23.5/Codex-Pace-macOS-arm64.zip) | Extract the ZIP and open **Codex Pace.app**. The menu bar follows the quota view you select. |
| Windows 11 on Intel or AMD | [Windows x64 1.23.5](https://github.com/ernest-fine/codex-pace/releases/download/v1.23.5/Codex-Pace-Windows-x64.zip) | Extract the ZIP and run `CodexPace.exe` from the extracted folder. |
| Windows 11 on Arm | [Windows Arm64 1.23.5](https://github.com/ernest-fine/codex-pace/releases/download/v1.23.5/Codex-Pace-Windows-arm64.zip) | Extract the ZIP and run `CodexPace.exe` from the extracted folder. |

The Windows ZIP includes the files the app needs. Keep them with `CodexPace.exe`. macOS may ask you to approve the app in Privacy & Security the first time you open it. [Apple’s first-launch guidance](https://support.apple.com/en-us/102445)

Keep Codex Pace running for background updates and notifications. Pace checks local readings every 30 seconds during work sessions and refreshes about every two minutes outside them. Use **Refresh** to check manually. See the [Windows guide](windows/README.md) for tray controls and setup details.

## Plan your quota

- Use **Used** or **Left** to choose how the app shows quota and targets.
- Choose **Session** or **Weekly** in the quota card. Session shows the change since the previous reading as a percentage of your session quota when both readings are available. On macOS, the Session view uses the share of weekly quota planned for that session. Using more than planned leaves less for the next session. Unused quota carries forward.
- Open **Edit sessions** on macOS or **Schedule → Edit sessions** on Windows. Choose **Edited days** to set each day’s hours or **Entire week** to use one schedule for all seven days. Use **Work days** to choose which days count toward your plan. The **Work sessions** table shows the quota target at the end of each session.
- Look under **Next turn** for a model and effort suggestion. For supported models, Pace suggests **Fast** when your recent pace leaves enough quota and **Standard** when you need to conserve it. Pace can also notify you once per target when your recent usage puts it within 30 minutes.
- Use **Set reset target** to plan for an earlier deadline. Redeem the reset in Codex at the time you choose.

Schedules and reset times use your computer’s local time zone. The app follows your computer’s light or dark appearance.

## How quota readings work

Codex Pace reads your signed-in account’s quota through Codex App Server. It can fall back to recent readings in the local Codex session history. The dashboard shows the source and age of the reading. Quota history, preferences, and your schedule are stored on your computer.

If Codex Pace needs help finding the CLI, set `CODEX_PACE_CODEX` to the full path of the native Codex executable.

## Build from source

**macOS** uses SwiftUI and the Xcode toolchain. Run this from the project root to create `Codex Pace.app`.

```sh
./build.sh
```

Run the macOS schedule checks from the project root:

```sh
test_dir=$(mktemp -d)
swiftc BudgetWarning.swift Controls.swift Pacing.swift ScheduleEditor.swift Telemetry.swift tests/WorkScheduleTests.swift -o "$test_dir/work-schedule-tests"
"$test_dir/work-schedule-tests"
```

**Windows** uses WinUI 3 and the .NET 10 SDK. On Windows, run this from the project root to create the x64 app folder.

```sh
dotnet publish windows/CodexPace.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o dist/windows-x64
```

For Windows on Arm, use `-r win-arm64 -p:Platform=ARM64 -o dist/windows-arm64`.
