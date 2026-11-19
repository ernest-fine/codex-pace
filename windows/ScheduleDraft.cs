namespace CodexPace;

internal enum ScheduleScope { EditedDays, EntireWeek }

internal readonly record struct ScheduleList(ScheduleScope Scope, DayOfWeek Day);

internal sealed class ScheduleDraft
{
    private const int LastMinuteOfDay = 1439;
    private static readonly DayOfWeek[] Days =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];
    private WorkSchedule editedSchedule;
    private SessionTemplate[]? sharedSessions;

    public ScheduleDraft(WorkSchedule schedule, IReadOnlyList<DayOfWeek> workdays, DateTimeOffset now)
    {
        editedSchedule = schedule;
        var today = WorkClock.Local(now).DayOfWeek;
        Day = workdays.Contains(today) ? today : Days.FirstOrDefault(workdays.Contains, today);
    }

    public DayOfWeek Day { get; private set; }
    public ScheduleScope Scope { get; private set; }
    public ScheduleList ActiveList => new(Scope, Day);
    public IReadOnlyList<SessionTemplate> Sessions => SessionsIn(ActiveList);
    public bool CanAddSession => NextSlot(Sessions) is not null;
    public WorkSchedule ScheduleToSave => Scope == ScheduleScope.EntireWeek
        ? WorkSchedule.FromSessions(Normalized(Sessions))
        : editedSchedule.DaySessions?.Keys.Aggregate(editedSchedule,
            (schedule, day) => schedule.WithDaySessions(day, Normalized(editedSchedule.EntriesOn(day))))
            ?? editedSchedule;

    public void SelectDay(DayOfWeek day)
    {
        if (!Enum.IsDefined(day)) throw new ArgumentOutOfRangeException(nameof(day));
        Day = day;
    }

    public void SelectScope(ScheduleScope scope)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (scope == ScheduleScope.EntireWeek)
            sharedSessions ??= editedSchedule.EntriesOn(Day).ToArray();
        Scope = scope;
    }

    public IReadOnlyList<SessionTemplate> SessionsIn(ScheduleList list) =>
        list.Scope == ScheduleScope.EntireWeek ? sharedSessions ?? [] : editedSchedule.EntriesOn(list.Day);

    public void UpdateSession(Guid id, Func<SessionTemplate, SessionTemplate> update, ScheduleList list)
    {
        var sessions = SessionsIn(list).ToArray();
        int index = Array.FindIndex(sessions, session => session.Id == id);
        if (index < 0) return;
        sessions[index] = update(sessions[index]);
        Replace(sessions, list);
    }

    public void RemoveSession(Guid id, ScheduleList list) =>
        Replace(SessionsIn(list).Where(session => session.Id != id).ToArray(), list);

    public void ApplyTemplate(SavedSessionTemplate template) =>
        Replace(template.Sessions.ToArray(), ActiveList);

    public Guid? AddSession()
    {
        if (NextSlot(Sessions) is not { } slot) return null;
        var session = new SessionTemplate(Guid.NewGuid(), $"Session {Sessions.Count + 1}", slot.Start, slot.End);
        Replace([.. Sessions, session], ActiveList);
        return session.Id;
    }

    private void Replace(SessionTemplate[] sessions, ScheduleList list)
    {
        if (list.Scope == ScheduleScope.EntireWeek) sharedSessions = sessions;
        else editedSchedule = editedSchedule.WithDaySessions(list.Day, sessions);
    }

    private static IEnumerable<SessionTemplate> Normalized(IEnumerable<SessionTemplate> sessions) =>
        sessions.Select(session => session with { Name = session.Name.Trim() }).OrderBy(session => session.StartMinute);

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
}
