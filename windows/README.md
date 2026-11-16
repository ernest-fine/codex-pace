# Codex Pace for Windows

1. Pick **x64** for an Intel or AMD Windows PC, or **ARM64** for a Windows on Arm PC. Windows lists its system type under **Settings → System → About**.
2. Extract the zip and run `CodexPace.exe`. The bundle includes its .NET Desktop runtime.
3. Sign in to Codex with your ChatGPT account on that PC, then use **Refresh**. Codex Pace reads the account quota through the native Codex CLI and can recover recent readings from its Windows session history.

The app is built for current Windows 11 releases. This is a portable, unsigned build.

Graphite follows changes to the Windows app appearance while Codex Pace is open. Session times and reset timestamps use the computer’s time zone.

## Using the tracker

- The session headline shows how much of the opening session allowance you’ve used beside the target by now. The bar is green through the target, amber up to 10% over it, and red above that. The allowance includes unused quota from earlier shifts.
- The tray icon’s tooltip shows the session, pace advice, and usage and target percentages. Double-click it to open the dashboard. Closing the window leaves the tracker active. Right-click the tray icon and choose **Quit** to exit.
- Use **Settings → Edit sessions** to add, name, and remove sessions. The same-day session times apply to the recurring days selected under **Settings → Work days**. The default is Monday through Friday.
- **Set reset target** sets a planning deadline. Redeem the reset in Codex.
- **Weekly quota** puts actual usage beside the target by now. The progress bar marks that target for the selected work days.
- The header’s **Used / Left** switch changes actual usage and targets in the session hero, Weekly quota card and Work sessions table.
- Budget warnings include the recommended model and effort. **Settings → Send test notification** checks the Windows notification path. Windows controls notification visibility under **Settings → System → Notifications**.

Settings and observed quota history stay in `%LOCALAPPDATA%\Codex Pace`. The Windows edition keeps its own local schedule and preferences.

If the tracker can only see local history, make sure the native Windows Codex CLI is available or set `CODEX_PACE_CODEX` to the full path of its `codex.exe`. A WSL-only or remote installation has separate session storage. `CodexPace.exe --demo` opens a sample dashboard.

[Codex on Windows](https://learn.chatgpt.com/docs/app/windows) · [.NET on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
