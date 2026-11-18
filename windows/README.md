# Codex Pace for Windows

1. Pick **x64** for an Intel or AMD Windows PC, or **ARM64** for a Windows on Arm PC. Windows lists its system type under **Settings → System → About**.
2. Extract the ZIP and run `CodexPace.exe` from that folder. Keep the other extracted files with it.
3. Sign in to Codex on that PC, then click **Refresh** in Codex Pace.

Codex Pace uses WinUI 3 on Windows 11 and follows Windows light or dark mode. Session and reset times use your computer’s local time zone.

## Using the tracker

- The session headline shows how much of the opening session allowance you’ve used beside the target by now. The bar is green through the target, amber up to 10% over it, and red above that. The allowance includes unused quota from earlier shifts.
- The tray icon shows a compact quota bar. Hover for the actual and target percentages, or click for the quota card. It follows **Session / Weekly** and **Used / Left** in the dashboard. Right-click the icon to open the dashboard or quit.
- Open **Schedule → Edit sessions**. Choose **Edited days** to set different hours for each day, or **Entire week** to apply one schedule to all seven days. Use **Work days** to choose which days count toward your quota plan.
- **Set reset target** sets a planning deadline. Redeem the reset in Codex.
- **Weekly quota** puts actual usage beside the target by now. The progress bar marks that target for the selected work days.
- Choose **Session** or **Weekly** in the quota card. Use **Used** or **Left** to choose how the app shows quota and targets.
- Look under **Next turn** for model and effort suggestions. Pace can also tell you to slow down when a fresh reading puts your session target or weekly limit within 30 minutes. Use **Schedule → Send test notification** to check that Windows can show alerts.

Settings and observed quota history stay in `%LOCALAPPDATA%\Codex Pace`. The Windows edition keeps its own local schedule and preferences.

If the tracker can only see local history, make sure the native Windows Codex CLI is available or set `CODEX_PACE_CODEX` to the full path of its `codex.exe`. A WSL-only or remote installation has separate session storage. `CodexPace.exe --demo` opens a sample dashboard.

[Codex on Windows](https://learn.chatgpt.com/docs/app/windows) · [.NET on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
