# Codex Ticker

Codex Ticker shows how much of your weekly Codex quota you’ve used and how much is available for each work session. It compares your usage with your plan and shows when to keep pace, slow down, or pause.

## Run Codex Ticker

Sign in to Codex on the computer where you’ll run the tracker.

| Platform | Download | How to run |
| --- | --- | --- |
| macOS 14 or later on Apple silicon | [macOS 1.23.14](https://github.com/ernest-fine/codex-ticker/releases/download/v1.23.14/Codex-Ticker-macOS-arm64.zip) | Extract the ZIP and open **Codex Ticker.app**. The menu bar follows the quota view you select. |
| Windows 11 on Intel or AMD | [Windows x64 1.23.14](https://github.com/ernest-fine/codex-ticker/releases/download/v1.23.14/Codex-Ticker-Windows-x64.zip) | Extract the ZIP and run `CodexTicker.exe` from the extracted folder. |
| Windows 11 on Arm | [Windows Arm64 1.23.14](https://github.com/ernest-fine/codex-ticker/releases/download/v1.23.14/Codex-Ticker-Windows-arm64.zip) | Extract the ZIP and run `CodexTicker.exe` from the extracted folder. |

The Windows ZIP includes the files the app needs. Keep them with `CodexTicker.exe`. macOS may ask you to approve the app in Privacy & Security the first time you open it. [Apple’s first-launch guidance](https://support.apple.com/en-us/102445)

Keep Codex Ticker running for background updates and notifications. Ticker checks local readings every 30 seconds during work sessions and refreshes about every two minutes outside them. Use **Refresh** to check manually. See the [Windows guide](windows/README.md) for tray controls and setup details.

## Plan your quota

- Use **Used** or **Left** to choose how the app shows quota and targets.
- Choose **Session** or **Weekly** in the quota card. Session shows the change since the previous reading as a percentage of your session quota when both readings are available. On macOS, the Session view uses the share of weekly quota planned for that session. Using more than planned leaves less for the next session. In **Used**, unused quota from earlier sessions shows up as **Carryover credit** until it’s spent. The dashboard and menu bar show remaining credit as a blue segment before the scheduled quota, with the target marker on the scheduled portion. **25% credit** is equivalent to **125% Left**. The signed Used balance is available in the carryover tooltip and menu bar details.
- Open **Edit sessions** on macOS or **Schedule → Edit sessions** on Windows. Choose **Edited days** to set each day’s hours or **Entire week** to use one schedule for all seven days. Use **Work days** to choose which days count toward your plan. The **Work sessions** table shows the quota target at the end of each session.
- In **Edit sessions**, choose **Save as template** to keep a named copy of the session names and hours on screen. Choose **Apply template** to fill the selected day or entire-week draft, then save the schedule to use it. Templates are stored locally and are available the next time you open Ticker.
- The macOS menu bar shows your rounded reading and target by now, such as `24/28`, above the progress bar. Open its menu for the exact reading, view, target, and pacing advice.
- **Pacing advice** tells you when to keep your pace, run fewer turns, save remaining quota for urgent work, or pause non-urgent work until the next session.
- Ticker can notify you once per target when recent usage puts it within 30 minutes.
- Use **Set reset target** to plan for an earlier deadline. Ticker keeps the date you choose across refreshes and restarts, including when Codex reports a new quota window. If the scheduled reset comes first, Ticker uses it and keeps your future target for the next window. Redeem the reset in Codex at the time you choose.

Schedules and reset times use your computer’s local time zone. The app follows your computer’s light or dark appearance.

## How quota readings work

Codex Ticker reads your signed-in account’s quota through Codex App Server. It can fall back to recent readings in the local Codex session history. The dashboard shows the source and age of the reading. Quota history, preferences, and your schedule are stored on your computer.

If Codex Ticker needs help finding the CLI, set `CODEX_TICKER_CODEX` to the full path of the native Codex executable.

## Build from source

**macOS** uses SwiftUI and the Xcode toolchain. Run this from the project root to create `Codex Ticker.app`.

```sh
./build.sh
```

Run the macOS schedule checks from the project root:

```sh
test_dir=$(mktemp -d)
swiftc BudgetWarning.swift Controls.swift Pacing.swift ScheduleEditor.swift Telemetry.swift tests/WorkScheduleTests.swift -o "$test_dir/work-schedule-tests"
"$test_dir/work-schedule-tests"
```

Run `./tests/manual-reset-refresh.sh` to check target persistence across refreshes and relaunches with isolated preferences and quota readings.

**Windows** uses WinUI 3 and the .NET 10 SDK. On Windows, run this from the project root to create the x64 app folder.

```sh
dotnet publish windows/CodexTicker.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o dist/windows-x64
```

For Windows on Arm, use `-r win-arm64 -p:Platform=ARM64 -o dist/windows-arm64`.
