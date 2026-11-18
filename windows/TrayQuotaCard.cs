using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.System;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace CodexPace;

internal sealed class TrayQuotaCard : IDisposable
{
    private const double CardWidth = 310;
    private const int ColorButtonFace = 15;
    private const int ColorHighlight = 13;
    private readonly Window window = new() { Title = "Codex Pace quota" };
    private readonly ContentControl root = new()
    {
        IsTabStop = true,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private readonly Border card;
    private readonly TextBlock title;
    private readonly TextBlock caption;
    private readonly TextBlock actual;
    private readonly TextBlock target;
    private readonly TextBlock note;
    private readonly Grid graph = new() { Height = 11, Margin = new Thickness(0, 9, 0, 0) };
    private readonly Border fill;
    private readonly Border marker;
    private readonly SolidColorBrush background = new();
    private readonly SolidColorBrush foreground = new();
    private readonly SolidColorBrush secondary = new();
    private readonly SolidColorBrush outline = new();
    private readonly SolidColorBrush track = new();
    private readonly SolidColorBrush tone = new();
    private readonly ThemeSettings appearance;
    private readonly UISettings systemColors = new();
    private readonly nint handle;
    private TrayQuotaReading? reading;
    private RectInt32 anchor;
    private bool isOpen;
    private bool wasActivated;
    private bool disposed;
    private long trayDismissedAt;

    public TrayQuotaCard()
    {
        Trace("Hit popup constructor");
        handle = WindowNative.GetWindowHandle(window);
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        window.AppWindow.SetPresenter(presenter);
        window.AppWindow.IsShownInSwitchers = false;
        Trace("Presenter configured");
        appearance = ThemeSettings.CreateForWindowId(window.AppWindow.Id);

        title = Text(17, true);
        caption = Text(24, true);
        caption.Margin = new Thickness(0, 7, 0, 0);
        caption.MaxLines = 2;
        AutomationProperties.SetLiveSetting(caption, AutomationLiveSetting.Polite);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        actual = Text(13, secondaryText: true);
        target = Text(13, secondaryText: true);
        target.HorizontalAlignment = HorizontalAlignment.Right;
        note = Text(13, secondaryText: true);
        note.Margin = new Thickness(0, 9, 0, 0);
        note.MaxLines = 3;

        graph.Children.Add(new Border
        {
            Height = 7, VerticalAlignment = VerticalAlignment.Center,
            Background = track, CornerRadius = new CornerRadius(4)
        });
        fill = new Border
        {
            Height = 7, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = tone, CornerRadius = new CornerRadius(4)
        };
        marker = new Border
        {
            Width = 2, Height = 11, HorizontalAlignment = HorizontalAlignment.Left,
            Background = foreground, CornerRadius = new CornerRadius(1)
        };
        graph.Children.Add(fill);
        graph.Children.Add(marker);
        graph.SizeChanged += (_, _) => RenderGraph();
        AutomationProperties.SetAccessibilityView(graph, AccessibilityView.Raw);

        var labels = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnSpacing = 8 };
        labels.ColumnDefinitions.Add(new ColumnDefinition());
        labels.ColumnDefinitions.Add(new ColumnDefinition());
        labels.Children.Add(actual);
        Grid.SetColumn(target, 1);
        labels.Children.Add(target);
        var content = new StackPanel();
        content.Children.Add(title);
        content.Children.Add(caption);
        content.Children.Add(graph);
        content.Children.Add(labels);
        content.Children.Add(note);
        card = new Border
        {
            Child = content, Background = background, BorderBrush = outline,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14)
        };
        root.Content = card;
        window.Content = root;
        root.KeyDown += KeyDown;
        root.ActualThemeChanged += ThemeChanged;
        appearance.Changed += ContrastChanged;
        window.Activated += Activated;
        window.AppWindow.Closing += Closing;
        root.Loaded += Loaded;
        ApplyAppearance();

        // Windows 11 rounds the borderless HWND to match the in-card surface.
        int cornerPreference = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
    }

    public bool IsVisible => !disposed && window.AppWindow.IsVisible;

    public void Toggle(TrayQuotaReading reading, RectInt32 anchor)
    {
        if (disposed) return;
        if (isOpen)
        {
            Hide();
            return;
        }
        // Clicking the icon first deactivates this window, before the tray callback arrives.
        if (trayDismissedAt != 0 &&
            Stopwatch.GetElapsedTime(trayDismissedAt) < TimeSpan.FromMilliseconds(GetDoubleClickTime() + 350))
        {
            trayDismissedAt = 0;
            return;
        }
        trayDismissedAt = 0;
        this.anchor = anchor;
        this.reading = reading;
        Trace("Rendering popup");
        Render();
        Trace("Positioning popup");
        Position(moveToMonitor: true);
        wasActivated = false;
        isOpen = true;
        Trace("Showing popup");
        window.AppWindow.Show();
        Trace("Activating popup");
        window.Activate();
        Trace("Popup activated");
    }

    public void Update(TrayQuotaReading reading)
    {
        if (disposed || !isOpen) return;
        this.reading = reading;
        Render();
        Position();
    }

    public void Hide()
    {
        if (disposed || !isOpen) return;
        isOpen = false;
        wasActivated = false;
        window.AppWindow.Hide();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        isOpen = false;
        appearance.Changed -= ContrastChanged;
        root.ActualThemeChanged -= ThemeChanged;
        root.KeyDown -= KeyDown;
        root.Loaded -= Loaded;
        window.Activated -= Activated;
        window.AppWindow.Closing -= Closing;
        if (root.XamlRoot is { } xamlRoot) xamlRoot.Changed -= XamlChanged;
        window.Close();
    }

    private void Render()
    {
        if (reading is null) return;
        title.Text = reading.Title;
        caption.Text = reading.Caption;
        Set(actual, reading.Actual is { } value ? $"Actual {value}" : null);
        Set(target, reading.Target is { } expected ? $"Target by now {expected}" : null);
        Set(note, reading.Note);
        ToolTipService.SetToolTip(note, reading.Note);
        ApplyAppearance();
        RenderGraph();
    }

    private void RenderGraph()
    {
        graph.Visibility = reading?.Amount is null ? Visibility.Collapsed : Visibility.Visible;
        if (reading?.Amount is not { } amount) return;
        fill.Width = graph.ActualWidth * Math.Clamp(amount, 0, 1);
        marker.Visibility = reading.Marker is null ? Visibility.Collapsed : Visibility.Visible;
        if (reading.Marker is { } mark)
            marker.Margin = new Thickness(Math.Clamp(graph.ActualWidth * Math.Clamp(mark, 0, 1) - 1,
                0, Math.Max(0, graph.ActualWidth - 2)), 0, 0, 0);
    }

    private void Position(bool moveToMonitor = false)
    {
        var display = DisplayArea.GetFromRect(anchor, DisplayAreaFallback.Nearest);
        Trace("Found display for popup");
        var work = display.WorkArea;
        var outer = display.OuterBounds;
        int anchorX = anchor.X - outer.X;
        int anchorY = anchor.Y - outer.Y;
        if (moveToMonitor)
        {
            // Moving while hidden gives GetDpiForWindow the tray monitor's scale.
            window.AppWindow.Move(new PointInt32(outer.X + work.X, outer.Y + work.Y));
        }
        uint dpi = GetDpiForWindow(handle);
        double scale = dpi == 0 ? 1 : dpi / 96.0;
        int gap = Math.Max(1, (int)Math.Round(8 * scale));
        int width = Math.Max(1, Math.Min((int)Math.Ceiling(CardWidth * scale), work.Width - 2 * gap));
        double logicalWidth = width / scale;
        Trace("Measuring popup");
        root.Measure(new Size(logicalWidth, double.PositiveInfinity));
        Trace("Measured popup");
        int height = Math.Max(1, Math.Min((int)Math.Ceiling(root.DesiredSize.Height * scale), work.Height - 2 * gap));
        int left = Math.Clamp(anchorX + anchor.Width - width, work.X + gap,
            Math.Max(work.X + gap, work.X + work.Width - width - gap));
        int above = anchorY - height - gap;
        int below = anchorY + anchor.Height + gap;
        int top = above >= work.Y + gap ? above
            : below + height <= work.Y + work.Height - gap ? below
            : Math.Clamp(above, work.Y + gap, Math.Max(work.Y + gap, work.Y + work.Height - height - gap));
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        if (position.X != outer.X + left || position.Y != outer.Y + top || size.Width != width || size.Height != height)
            window.AppWindow.MoveAndResize(new RectInt32(left, top, width, height), display);
    }

    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (root.XamlRoot is { } xamlRoot)
        {
            xamlRoot.Changed -= XamlChanged;
            xamlRoot.Changed += XamlChanged;
        }
        Trace("Popup loaded");
        if (isOpen) Position();
    }

    private void XamlChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        Trace("Popup XAML root changed");
        if (isOpen && !disposed) Position();
    }

    private void Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            wasActivated = true;
            if (isOpen) root.Focus(FocusState.Programmatic);
            return;
        }
        if (!isOpen || !wasActivated) return;
        if (GetCursorPos(out var cursor) && cursor.X >= anchor.X && cursor.X < anchor.X + anchor.Width
            && cursor.Y >= anchor.Y && cursor.Y < anchor.Y + anchor.Height)
            trayDismissedAt = Stopwatch.GetTimestamp();
        Hide();
    }

    private void Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (disposed) return;
        args.Cancel = true;
        Hide();
    }

    private void KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        args.Handled = true;
        Hide();
    }

    private void ThemeChanged(FrameworkElement sender, object args) => ApplyAppearance();

    private void ContrastChanged(ThemeSettings sender, object args) =>
        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (!disposed) ApplyAppearance();
        });

    private void ApplyAppearance()
    {
        if (appearance.HighContrast)
        {
            background.Color = systemColors.GetColorValue(UIColorType.Background);
            foreground.Color = secondary.Color = outline.Color = systemColors.GetColorValue(UIColorType.Foreground);
            track.Color = SystemColor(ColorButtonFace);
            tone.Color = SystemColor(ColorHighlight);
        }
        else
        {
            bool dark = root.ActualTheme == ElementTheme.Dark;
            background.Color = ColorFor(dark ? 0x222723 : 0xfdfefc);
            foreground.Color = ColorFor(dark ? 0xf3f4f1 : 0x252826);
            secondary.Color = ColorFor(dark ? 0xbac2bd : 0x515a53);
            outline.Color = ColorFor(dark ? 0x424c44 : 0xd5ded6);
            track.Color = ColorFor(dark ? 0x4b574d : 0xd5ddd5);
            tone.Color = reading?.Tone switch
            {
                TrayQuotaTone.WithinTarget => ColorFor(dark ? 0x8ac99e : 0x287653),
                TrayQuotaTone.SlightlyOverTarget => ColorFor(dark ? 0xe2ba73 : 0x946217),
                TrayQuotaTone.OverTarget => ColorFor(dark ? 0xefa5a9 : 0xa93846),
                _ => secondary.Color
            };
        }
        string status = reading?.Tone switch
        {
            TrayQuotaTone.WithinTarget => "Within target",
            TrayQuotaTone.SlightlyOverTarget => "Slightly over target",
            TrayQuotaTone.OverTarget => "Over target",
            _ => "Quota status unavailable"
        };
        AutomationProperties.SetName(root, reading is null ? "Codex Pace quota"
            : $"{reading.Title}. {reading.Caption}. {status}. {reading.Note}");
    }

    private TextBlock Text(double size, bool strong = false, bool secondaryText = false) => new()
    {
        FontSize = size,
        FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = secondaryText ? secondary : foreground,
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private static void Set(TextBlock text, string? value)
    {
        text.Text = value ?? string.Empty;
        text.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void Trace(string message)
    {
        if (Environment.GetEnvironmentVariable("CODEX_PACE_TRAY_CHECK_LOG") is { } log)
            File.AppendAllText(log, message + Environment.NewLine);
    }

    private static Color ColorFor(int rgb) =>
        Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color SystemColor(int index)
    {
        uint rgb = GetSysColor(index);
        return Color.FromArgb(255, (byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16));
    }

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
}
