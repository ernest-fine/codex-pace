using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace CodexTicker;

internal static class SettingsDialogs
{
    private static readonly DayOfWeek[] Days =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public static async Task<WorkSchedule?> EditSessionsAsync(
        XamlRoot root, Preferences preferences, DateTimeOffset now, Action<SavedSessionTemplate[]> saveTemplates)
    {
        var workdays = preferences.Workdays;
        var draft = new ScheduleDraft(preferences.Schedule, workdays, now);
        var body = new StackPanel { Spacing = 12 };
        var editedDays = new ToggleButton { Content = "Edited days", IsChecked = true };
        var entireWeek = new ToggleButton { Content = "Entire week" };
        var scope = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        scope.Children.Add(new TextBlock { Text = "Apply to", VerticalAlignment = VerticalAlignment.Center });
        scope.Children.Add(editedDays);
        scope.Children.Add(entireWeek);
        AutomationProperties.SetName(scope, "Apply to");
        var dayBar = new Grid { ColumnSpacing = 4 };
        AutomationProperties.SetName(dayBar, "Day");
        var offDay = Message();
        var fields = new ScrollViewer
        {
            MaxHeight = 300,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var add = new Button { Content = "Add session" };
        var templates = new SessionTemplateControls(preferences.SessionTemplates, () => draft.Sessions, saveTemplates);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(add);
        actions.Children.Add(templates.SaveButton);
        actions.Children.Add(templates.ApplyButton);
        var weekNote = Message();
        var error = Message();
        body.Children.Add(scope);
        body.Children.Add(dayBar);
        body.Children.Add(offDay);
        body.Children.Add(fields);
        body.Children.Add(actions);
        body.Children.Add(weekNote);
        body.Children.Add(error);
        var dialog = Create(root, "Edit sessions", "Save schedule", body);
        var dayButtons = new Dictionary<DayOfWeek, ToggleButton>();

        string DayName(DayOfWeek day) => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day);

        bool Validate()
        {
            var proposed = draft.ScheduleToSave;
            bool valid = proposed.IsValid;
            dialog.IsPrimaryButtonEnabled = valid;
            add.IsEnabled = draft.CanAddSession;
            templates.UpdateAvailability();
            string? validation = valid ? null : "Give each session a name and valid hours that don’t overlap.";
            if (!valid && draft.Scope == ScheduleScope.EditedDays)
            {
                var invalid = Days.Where(day => !WorkSchedule.FromSessions(proposed.EntriesOn(day)).IsValid);
                validation = $"Check {string.Join(", ", invalid.Select(DayName))}. {validation}";
            }
            SetMessage(error, validation);
            return valid;
        }

        void Update(Guid id, Func<SessionTemplate, SessionTemplate> update, ScheduleList list)
        {
            draft.UpdateSession(id, update, list);
            Validate();
        }

        void Render(Guid? focusSession = null)
        {
            bool wholeWeek = draft.Scope == ScheduleScope.EntireWeek;
            editedDays.IsChecked = !wholeWeek;
            entireWeek.IsChecked = wholeWeek;
            dayBar.Visibility = wholeWeek ? Visibility.Collapsed : Visibility.Visible;
            foreach (var (day, button) in dayButtons) button.IsChecked = day == draft.Day;
            SetMessage(offDay, !wholeWeek && !workdays.Contains(draft.Day)
                ? $"{DayName(draft.Day)} is off in Work days." : null);
            SetMessage(weekNote, wholeWeek
                ? draft.Sessions.Count == 0 ? "Clears sessions for all days." : "Replaces schedules for all days."
                : null);
            dialog.PrimaryButtonText = wholeWeek ? "Apply to entire week" : "Save schedule";
            var list = draft.ActiveList;
            var sessions = draft.SessionsIn(list);
            if (sessions.Count == 0)
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
            for (int index = 0; index < sessions.Count; index++)
            {
                var session = sessions[index];
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
                name.TextChanged += (_, _) => Update(session.Id, value => value with { Name = name.Text }, list);
                start.TimeChanged += (_, _) => Update(session.Id, value => value with { StartMinute = Minutes(start) }, list);
                end.TimeChanged += (_, _) => Update(session.Id, value => value with { EndMinute = Minutes(end) }, list);
                remove.Click += (_, _) =>
                {
                    int removed = draft.SessionsIn(list).ToList().FindIndex(entry => entry.Id == session.Id);
                    if (removed < 0) return;
                    draft.RemoveSession(session.Id, list);
                    var remaining = draft.Sessions;
                    Render(remaining.Count == 0 ? null : remaining[Math.Min(removed, remaining.Count - 1)].Id);
                    if (remaining.Count == 0) add.Focus(FocusState.Programmatic);
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
                    focus.StartBringIntoView();
                };
            }
            fields.Content = grid;
            Validate();
        }

        for (int index = 0; index < Days.Length; index++)
        {
            var day = Days[index];
            dayBar.ColumnDefinitions.Add(new ColumnDefinition());
            var name = DayName(day);
            var button = new ToggleButton
            {
                Content = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0,
                Padding = new Thickness(5, 8, 5, 8)
            };
            AutomationProperties.SetName(button, workdays.Contains(day) ? name : $"{name}, off");
            ToolTipService.SetToolTip(button, workdays.Contains(day) ? name : $"{name} is off in Work days");
            button.Click += (_, _) =>
            {
                draft.SelectDay(day);
                Render();
            };
            dayButtons.Add(day, button);
            Place(dayBar, button, 0, index);
        }
        editedDays.Click += (_, _) =>
        {
            draft.SelectScope(ScheduleScope.EditedDays);
            Render();
        };
        entireWeek.Click += (_, _) =>
        {
            draft.SelectScope(ScheduleScope.EntireWeek);
            Render();
        };
        add.Click += (_, _) =>
        {
            if (draft.AddSession() is { } id) Render(id);
        };
        templates.TemplateSelected += template =>
        {
            draft.ApplyTemplate(template);
            Render();
        };
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !Validate();
        Render();
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? draft.ScheduleToSave : null;
    }

    public static async Task<DayOfWeek[]?> EditWorkdaysAsync(XamlRoot root, IReadOnlyList<DayOfWeek> workdays)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int index = 0; index < (Days.Length + 1) / 2; index++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var options = Days.Select((day, index) =>
        {
            var name = CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day);
            var label = new TextBlock { Text = name, FontSize = 15, TextWrapping = TextWrapping.Wrap };
            var check = new TextBlock { Text = "✓", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetAccessibilityView(check, AccessibilityView.Raw);
            var content = new Grid { ColumnSpacing = 6 };
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Place(content, label, 0, 0);
            Place(content, check, 0, 1);

            var tile = new ToggleButton
            {
                Content = content,
                IsChecked = workdays.Contains(day),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = 40,
                Padding = new Thickness(9, 6, 9, 6),
            };
            AutomationProperties.SetName(tile, name);
            tile.Checked += (_, _) => RenderSelection();
            tile.Unchecked += (_, _) => RenderSelection();
            RenderSelection();
            Place(grid, tile, index / 2, index % 2);
            return tile;

            void RenderSelection()
            {
                check.Visibility = tile.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                label.FontWeight = tile.IsChecked == true ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            }
        }).ToArray();

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "Choose which days count toward your quota plan.", FontSize = 15, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(grid);

        var dialog = Create(root, "Work days", "Save work days", body);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return Days.Where((_, index) => options[index].IsChecked == true).ToArray();
    }

    public static async Task<(bool Applied, DateTimeOffset? Target)> EditResetTargetAsync(
        XamlRoot root, Func<DateTimeOffset?> currentReset, DateTimeOffset? existingTarget, Func<DateTimeOffset> now)
    {
        var next = NextMinute(now());
        var scheduledReset = currentReset();
        if (existingTarget is null && (scheduledReset is null || next >= scheduledReset)) return (false, null);
        var initial = WorkClock.Local(existingTarget ?? (next.AddMinutes(30) < scheduledReset ? next.AddMinutes(30) : next));
        var body = new StackPanel { Spacing = 10 };
        var date = new CalendarDatePicker { Header = "Date", Date = initial };
        var time = HourPicker("Time", (int)initial.TimeOfDay.TotalMinutes);
        time.Header = "Time";
        var error = Message();
        var scheduled = new TextBlock { TextWrapping = TextWrapping.Wrap };
        body.Children.Add(scheduled);
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
            var reset = currentReset();
            bool future = proposed > now();
            scheduled.Text = reset is { } dateTime
                ? $"Scheduled account reset · {WorkClock.Local(dateTime).ToString("ddd, d MMM HH:mm", CultureInfo.CurrentCulture)}"
                : "Scheduled account reset unavailable";
            string? message = !future
                ? "Choose a date and time in the future."
                : reset is null || proposed >= reset ? "Choose a time before the scheduled account reset." : null;
            // A refresh can move the reset while the dialog is open. Recheck it on Save.
            dialog.IsPrimaryButtonEnabled = future;
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
            Time = TimeSpan.FromMinutes(Math.Clamp(minutes, 0, 1439)),
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

    private static DateTimeOffset NextMinute(DateTimeOffset instant)
    {
        long ticks = instant.UtcTicks;
        return new DateTimeOffset(ticks - ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero).AddMinutes(1);
    }
}
