using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace CodexPace;

internal static class SettingsDialogs
{
    private const int LastMinuteOfDay = 1439;
    private static readonly DayOfWeek[] Days =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public static async Task<WorkSchedule?> EditSessionsAsync(XamlRoot root, WorkSchedule schedule)
    {
        var draft = schedule.Entries.OrderBy(session => session.StartMinute).ToList();
        var body = new StackPanel { Spacing = 12 };
        var fields = new ScrollViewer
        {
            MaxHeight = 300,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var add = new Button { Content = "Add session" };
        var error = Message();
        body.Children.Add(fields);
        body.Children.Add(add);
        body.Children.Add(error);
        var dialog = Create(root, "Edit sessions", "Save schedule", body);

        WorkSchedule Proposed() => WorkSchedule.FromSessions(draft
            .Select(session => session with { Name = session.Name.Trim() })
            .OrderBy(session => session.StartMinute));

        bool Validate()
        {
            bool valid = Proposed().IsValid;
            dialog.IsPrimaryButtonEnabled = valid;
            add.IsEnabled = NextSlot(draft) is not null;
            SetMessage(error, valid ? null : "Give each session a name and valid hours that don’t overlap.");
            return valid;
        }

        void Update(Guid id, Func<SessionTemplate, SessionTemplate> update)
        {
            int index = draft.FindIndex(entry => entry.Id == id);
            if (index < 0) return;
            draft[index] = update(draft[index]);
            Validate();
        }

        void Render(Guid? focusSession = null)
        {
            if (draft.Count == 0)
            {
                fields.Content = new TextBlock { Text = "No sessions" };
                Validate();
                return;
            }

            var grid = new Grid { ColumnSpacing = 8, RowSpacing = 9 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 90 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(106) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(106) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Place(grid, new TextBlock { Text = "Session name" }, 0, 0);
            Place(grid, new TextBlock { Text = "Start" }, 0, 1);
            Place(grid, new TextBlock { Text = "End" }, 0, 2);

            TextBox? focus = null;
            for (int index = 0; index < draft.Count; index++)
            {
                var session = draft[index];
                int row = index + 1;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = new TextBox
                {
                    Text = session.Name, PlaceholderText = "Session name", MinWidth = 0
                };
                AutomationProperties.SetName(name, $"Session {row} name");
                var start = HourPicker($"Session {row} start", session.StartMinute);
                var end = HourPicker($"Session {row} end", session.EndMinute);
                var remove = new Button
                {
                    Content = new SymbolIcon(Symbol.Delete),
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(remove, $"Remove session {row}");
                ToolTipService.SetToolTip(remove, "Remove session");
                name.TextChanged += (_, _) => Update(session.Id, value => value with { Name = name.Text });
                start.TimeChanged += (_, _) => Update(session.Id, value => value with { StartMinute = Minutes(start) });
                end.TimeChanged += (_, _) => Update(session.Id, value => value with { EndMinute = Minutes(end) });
                remove.Click += (_, _) =>
                {
                    int removed = draft.FindIndex(entry => entry.Id == session.Id);
                    if (removed < 0) return;
                    draft.RemoveAt(removed);
                    Render(draft.Count == 0 ? null : draft[Math.Min(removed, draft.Count - 1)].Id);
                    if (draft.Count == 0) add.Focus(FocusState.Programmatic);
                };
                Place(grid, name, row, 0);
                Place(grid, start, row, 1);
                Place(grid, end, row, 2);
                Place(grid, remove, row, 3);
                if (session.Id == focusSession) focus = name;
            }
            if (focus is not null)
            {
                focus.Loaded += (_, _) =>
                {
                    focus.Focus(FocusState.Programmatic);
                    focus.SelectAll();
                };
            }
            fields.Content = grid;
            Validate();
        }

        add.Click += (_, _) =>
        {
            if (NextSlot(draft) is not { } slot) return;
            var session = new SessionTemplate(Guid.NewGuid(), $"Session {draft.Count + 1}", slot.Start, slot.End);
            draft.Add(session);
            Render(session.Id);
        };
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !Validate();
        Render();
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? Proposed() : null;
    }

    public static async Task<DayOfWeek[]?> EditWorkdaysAsync(XamlRoot root, IReadOnlyList<DayOfWeek> workdays)
    {
        var body = new StackPanel { Spacing = 2 };
        var options = Days.Select(day => new CheckBox
        {
            Content = CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day),
            IsChecked = workdays.Contains(day)
        }).ToArray();
        foreach (var option in options) body.Children.Add(option);

        var dialog = Create(root, "Work days", "Save work days", body);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return Days.Where((_, index) => options[index].IsChecked == true).ToArray();
    }

    public static async Task<(bool Applied, DateTimeOffset? Target)> EditResetTargetAsync(
        XamlRoot root, DateTimeOffset scheduledReset, DateTimeOffset? existingTarget, Func<DateTimeOffset> now)
    {
        var next = NextMinute(now());
        if (next >= scheduledReset) return (false, null);
        var initial = WorkClock.Local(existingTarget ?? (next.AddMinutes(30) < scheduledReset ? next.AddMinutes(30) : next));
        var body = new StackPanel { Spacing = 10 };
        var date = new CalendarDatePicker { Header = "Date", Date = initial };
        var time = HourPicker("Time", (int)initial.TimeOfDay.TotalMinutes);
        time.Header = "Time";
        var error = Message();
        body.Children.Add(new TextBlock
        {
            Text = $"Scheduled account reset · {WorkClock.Local(scheduledReset).ToString("ddd, d MMM HH:mm", CultureInfo.CurrentCulture)}",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(date);
        body.Children.Add(time);
        body.Children.Add(error);

        var dialog = Create(root, "Manual reset target", "Save target", body);
        if (existingTarget is not null) dialog.SecondaryButtonText = "Clear target";

        DateTimeOffset? Proposed() => date.Date is { } selected
            ? WorkClock.FromLocal(selected.Date + time.Time)
            : null;

        bool Validate()
        {
            var proposed = Proposed();
            string? message = proposed is null || proposed <= now()
                ? "Choose a date and time in the future."
                : proposed >= scheduledReset ? "Choose a time before the scheduled account reset." : null;
            dialog.IsPrimaryButtonEnabled = message is null;
            SetMessage(error, message);
            return message is null;
        }

        date.DateChanged += (_, _) => Validate();
        time.TimeChanged += (_, _) => Validate();
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !Validate();
        Validate();
        return (await dialog.ShowAsync()) switch
        {
            ContentDialogResult.Primary => (true, Proposed()),
            ContentDialogResult.Secondary => (true, null),
            _ => (false, null)
        };
    }

    private static ContentDialog Create(XamlRoot root, string title, string action, FrameworkElement body) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = body,
        PrimaryButtonText = action,
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary
    };

    private static TimePicker HourPicker(string name, int minutes)
    {
        var picker = new TimePicker
        {
            ClockIdentifier = "24HourClock",
            Time = TimeSpan.FromMinutes(Math.Clamp(minutes, 0, LastMinuteOfDay)),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(picker, name);
        return picker;
    }

    private static int Minutes(TimePicker picker) => (int)picker.Time.TotalMinutes;

    private static TextBlock Message()
    {
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
        return message;
    }

    private static void SetMessage(TextBlock element, string? text)
    {
        element.Text = text ?? string.Empty;
        element.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static (int Start, int End)? NextSlot(IReadOnlyList<SessionTemplate> sessions)
    {
        if (sessions.Count == 0) return (570, 630);

        (int Start, int End)? lastGap = null;
        int cursor = 0;
        foreach (var session in sessions.OrderBy(session => session.StartMinute))
        {
            if (cursor < session.StartMinute)
                lastGap = (cursor, Math.Min(cursor + 60, session.StartMinute));
            cursor = Math.Max(cursor, session.EndMinute);
        }
        return cursor < LastMinuteOfDay ? (cursor, Math.Min(cursor + 60, LastMinuteOfDay)) : lastGap;
    }

    private static DateTimeOffset NextMinute(DateTimeOffset instant)
    {
        long ticks = instant.UtcTicks;
        return new DateTimeOffset(ticks - ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero).AddMinutes(1);
    }
}
