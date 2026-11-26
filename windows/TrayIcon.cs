using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace CodexTicker;

internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8001; // WM_APP + 1
    private const uint DestroyWindowMessage = 0x0082; // WM_NCDESTROY
    private const uint IconAdd = 0;
    private const uint IconModify = 1;
    private const uint IconDelete = 2;
    private const uint IconSetVersion = 4;
    private const uint Version4 = 4;
    private const uint NotifyMessage = 0x01;
    private const uint NotifyIcon = 0x02;
    private const uint NotifyTip = 0x04;
    private const uint NotifyWarning = 0x10;
    private const uint NotifyShowTip = 0x80;
    private const uint ContextMenu = 0x007B; // WM_CONTEXTMENU
    private const uint DoubleClick = 0x0203; // WM_LBUTTONDBLCLK
    private const uint LeftClick = 0x0202; // WM_LBUTTONUP
    private const uint RightClick = 0x0205; // WM_RBUTTONUP
    private const uint Select = 0x0400; // NIN_SELECT
    private const uint KeySelect = 0x0401; // NIN_KEYSELECT
    private const uint BalloonClick = 0x0405; // NIN_BALLOONUSERCLICK
    private const uint MenuDisabled = 0x0003;
    private const uint MenuSeparator = 0x0800;
    private const uint PopupFlags = 0x0002 | 0x0008 | 0x0020 | 0x0080 | 0x0100;
    private const uint OpenCommand = 1;
    private const uint RefreshCommand = 2;
    private const uint TestCommand = 3;
    private const uint QuitCommand = 4;

    private readonly nint window;
    private readonly DispatcherQueue dispatcher;
    private readonly SubclassProcedure callback;
    private readonly uint taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly nint icon;
    private readonly nint smallIcon;
    private readonly nint largeIcon;
    private readonly bool subclassInstalled;
    private readonly DispatcherQueueTimer clickTimer;
    private nint meterIcon;
    private (double? Amount, double? Marker, TrayQuotaTone Tone, int Size)? meterState;
    private RectInt32 pendingBounds;
    private long ignoreSelectUntil;
    private string caption = "Codex Ticker";
    private bool refreshing;
    private bool registered;
    private bool usesVersion4;
    private bool disposed;
    private bool showingMenu;

    public event Action? OpenRequested;
    public event Action<RectInt32>? QuotaRequested;
    public event Action? RefreshRequested;
    public event Action? TestNotificationRequested;
    public event Action? QuitRequested;

    public bool IsRegistered => registered;

    public TrayIcon(nint window, DispatcherQueue dispatcher)
    {
        this.window = window;
        this.dispatcher = dispatcher;
        clickTimer = dispatcher.CreateTimer();
        clickTimer.IsRepeating = false;
        clickTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        clickTimer.Tick += (_, _) =>
        {
            clickTimer.Stop();
            if (!disposed) QuotaRequested?.Invoke(pendingBounds);
        };
        callback = WindowProcedure;
        if (Environment.ProcessPath is { } executable)
            ExtractIconExW(executable, 0, out largeIcon, out smallIcon, 1);
        icon = smallIcon != 0 ? smallIcon : largeIcon != 0 ? largeIcon : LoadIconW(0, 32512);
        if (smallIcon != 0) SendMessageW(window, 0x0080, 0, smallIcon); // WM_SETICON, ICON_SMALL
        if (largeIcon != 0) SendMessageW(window, 0x0080, 1, largeIcon); // WM_SETICON, ICON_BIG
        subclassInstalled = SetWindowSubclass(window, callback, IconId, 0);
        Register();
    }

    public void SelectForDemo() => SendMessageW(window, CallbackMessage, 0, (nint)((IconId << 16) | Select));

    public void SetStatus(TrayQuotaReading reading, bool refreshing)
    {
        if (disposed) return;
        caption = Limit(reading.Tooltip, 127);
        this.refreshing = refreshing;
        UpdateMeter(reading);
        if (!registered)
        {
            Register();
            return;
        }
        var data = CreateNotificationData(NotifyTip | NotifyShowTip | NotifyIcon);
        if (!Shell_NotifyIconW(IconModify, ref data))
        {
            registered = false;
            Register();
        }
    }

    public bool ShowWarning(string title, string message)
    {
        if (!registered) Register();
        if (!registered) return false;

        var data = CreateNotificationData(NotifyWarning);
        data.InfoTitle = Limit(title, 63);
        data.Info = Limit(message, 255);
        data.InfoFlags = 2; // NIIF_WARNING
        return Shell_NotifyIconW(IconModify, ref data);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        clickTimer.Stop();
        if (registered)
        {
            var data = CreateNotificationData(0);
            Shell_NotifyIconW(IconDelete, ref data);
            registered = false;
        }
        if (subclassInstalled) RemoveWindowSubclass(window, callback, IconId);
        if (meterIcon != 0) DestroyIcon(meterIcon);
        if (smallIcon != 0) DestroyIcon(smallIcon);
        if (largeIcon != 0 && largeIcon != smallIcon) DestroyIcon(largeIcon);
    }

    private void Register()
    {
        if (disposed || !subclassInstalled || icon == 0) return;
        var data = CreateNotificationData(NotifyMessage | NotifyIcon | NotifyTip | NotifyShowTip);
        registered = Shell_NotifyIconW(IconAdd, ref data);
        if (!registered) return;
        data.TimeoutOrVersion = Version4;
        usesVersion4 = Shell_NotifyIconW(IconSetVersion, ref data);
    }

    private NotifyIconData CreateNotificationData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = window,
        Id = IconId,
        Flags = flags,
        Callback = CallbackMessage,
        Icon = meterIcon != 0 ? meterIcon : icon,
        ToolTip = caption,
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private nint WindowProcedure(nint handle, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        if (message == DestroyWindowMessage)
            Dispose();
        else if (taskbarCreated != 0 && message == taskbarCreated)
        {
            registered = false;
            dispatcher.TryEnqueue(Register);
        }
        else if (message == CallbackMessage)
        {
            // The low word contains the shell event even when the high word contains the version 4 icon ID.
            var trayEvent = (uint)((nuint)lParam & 0xffff);
            dispatcher.TryEnqueue(() => HandleTrayEvent(trayEvent, wParam));
            return 0;
        }
        return DefSubclassProc(handle, message, wParam, lParam);
    }

    private void HandleTrayEvent(uint trayEvent, nuint anchor)
    {
        if (disposed) return;
        if (trayEvent == ContextMenu || (!usesVersion4 && trayEvent == RightClick))
        {
            clickTimer.Stop();
            ShowMenu();
        }
        else if (trayEvent is BalloonClick or DoubleClick)
        {
            clickTimer.Stop();
            ignoreSelectUntil = Environment.TickCount64 + GetDoubleClickTime();
            OpenRequested?.Invoke();
        }
        else if (trayEvent == KeySelect)
        {
            clickTimer.Stop();
            QuotaRequested?.Invoke(IconBounds(anchor));
        }
        else if ((trayEvent == Select || (!usesVersion4 && trayEvent == LeftClick))
            && Environment.TickCount64 >= ignoreSelectUntil)
        {
            pendingBounds = IconBounds(anchor);
            clickTimer.Stop();
            clickTimer.Start();
        }
    }

    private void UpdateMeter(TrayQuotaReading reading)
    {
        int size = Math.Clamp(GetSystemMetrics(49), 16, 64); // SM_CXSMICON
        var nextState = (reading.Amount, reading.Marker, reading.Tone, size);
        if (meterState == nextState) return;
        nint nextIcon = TrayMeterIcon.Create(reading, size);
        var previousIcon = meterIcon;
        meterIcon = nextIcon;
        // The shell copies the supplied HICON when adding or updating the notification-area icon.
        if (registered)
        {
            var data = CreateNotificationData(NotifyIcon);
            if (!Shell_NotifyIconW(IconModify, ref data)) registered = false;
        }
        if (previousIcon != 0) DestroyIcon(previousIcon);
        meterState = nextIcon != 0 || reading.Amount is null ? nextState : null;
    }

    private RectInt32 IconBounds(nuint anchor)
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = window, Id = IconId
        };
        if (Shell_NotifyIconGetRect(ref identifier, out var bounds) >= 0)
            return new RectInt32(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);

        var point = new Point { X = unchecked((short)(anchor & 0xffff)), Y = unchecked((short)((anchor >> 16) & 0xffff)) };
        if (!usesVersion4 || (point.X == -1 && point.Y == -1)) GetCursorPos(out point);
        int size = Math.Max(16, GetSystemMetrics(49));
        return new RectInt32(point.X - size / 2, point.Y - size / 2, size, size);
    }

    private void ShowMenu()
    {
        if (showingMenu) return;
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        showingMenu = true;
        uint command;
        try
        {
            AppendMenuW(menu, MenuDisabled, 0, caption.Replace("&", "&&", StringComparison.Ordinal));
            AppendMenuW(menu, MenuSeparator, 0, null);
            AppendMenuW(menu, 0, OpenCommand, "Open dashboard");
            AppendMenuW(menu, refreshing ? MenuDisabled : 0, RefreshCommand, "Refresh usage");
            AppendMenuW(menu, 0, TestCommand, "Send test notification");
            AppendMenuW(menu, MenuSeparator, 0, null);
            AppendMenuW(menu, 0, QuitCommand, "Quit");

            var identifier = new NotifyIconIdentifier
            {
                Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = window, Id = IconId
            };
            GetCursorPos(out var position);
            if (Shell_NotifyIconGetRect(ref identifier, out var bounds) >= 0)
                position = new Point { X = (bounds.Left + bounds.Right) / 2, Y = bounds.Top };

            SetForegroundWindow(window);
            command = TrackPopupMenuEx(menu, PopupFlags, position.X, position.Y, window, 0);
            // The shell menu needs a queued message after dismissal to open correctly next time.
            PostMessageW(window, 0, 0, 0); // WM_NULL
        }
        finally
        {
            showingMenu = false;
            DestroyMenu(menu);
        }
        if (disposed) return;
        switch (command)
        {
            case OpenCommand: OpenRequested?.Invoke(); break;
            case RefreshCommand: RefreshRequested?.Invoke(); break;
            case TestCommand: TestNotificationRequested?.Invoke(); break;
            case QuitCommand: QuitRequested?.Invoke(); break;
        }
    }

    private static string Limit(string text, int limit)
    {
        if (text.Length <= limit) return text;
        return text[..(char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit)];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ToolTip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public Guid GuidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure callback, nuint id, nuint reference);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure callback, nuint id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint operation, ref NotifyIconData data);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect bounds);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconExW(string executable, int index, out nint large, out nint small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint LoadIconW(nint module, nint name);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? title);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point position);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);
}
