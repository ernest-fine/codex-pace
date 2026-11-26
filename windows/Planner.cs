using System.Globalization;

namespace CodexTicker;

internal static class Planner
{
    private static readonly TimeSpan QuotaFreshness = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan NotificationFreshness = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NotificationHorizon = TimeSpan.FromMinutes(30);

    public static DashboardData Calculate(
        Telemetry? telemetry, IReadOnlyList<QuotaSample> history, Preferences preferences, DateTimeOffset now)
    {
        var quota = telemetry?.Quota;
        var shortQuota = telemetry?.ShortQuota;
        if (shortQuota?.ResetsAt <= now) shortQuota = null;
        var activity = telemetry?.Activity;
        if (activity is not null && (activity.ObservedAt > now || now - activity.ObservedAt > TimeSpan.FromHours(1)))
            activity = null;
        var models = telemetry?.Models ?? [];

        DateTimeOffset? deadline = null;
        Pace? pace = null;
        if (quota is not null && quota.ResetsAt > now)
        {
            deadline = preferences.ResetTarget?.ActiveDeadline(now, quota.ResetsAt);
            pace = CalculatePace(quota, history, preferences, deadline ?? quota.ResetsAt, now);
        }

        var guidance = pace is null || quota is null ? null : GuideSession(pace, quota, now);
        var (status, recommendation, detail, warning, show) = Recommend(quota, pace, guidance, deadline, now);
        return new DashboardData(
            quota, shortQuota, pace, guidance, status, recommendation, detail, ActivityCaption(activity, models),
            telemetry?.Source ?? "Connecting to Codex", telemetry?.Error, warning)
        {
            SessionDelta = SessionQuotaDelta.Calculate(quota, pace, history, now),
            ShowsRecommendation = show
        };
    }

    public static IEnumerable<BudgetWarning> ImmediateWarnings(QuotaSample? quota, Pace? pace, DateTimeOffset now)
    {
        if (quota is null || quota.UsedPercent is not (>= 0 and <= 100)
            || quota.ResetsAt <= now || quota.ObservedAt > now
            || now - quota.ObservedAt > NotificationFreshness) yield break;

        if (quota.UsedPercent >= 100)
        {
            yield return MakeWarning(quota, "Pause non-urgent turns",
                "You’ve reached your weekly Codex limit.", quota.ResetsAt) with { Title = "Codex weekly limit reached" };
            yield break;
        }

        if (pace is null) yield break;

        var imminent = new List<(TimeSpan UntilTarget, BudgetWarning Warning)>();
        if (pace.CurrentSession is { } session && session.Start <= now && now < session.End
            && pace.SessionRoomToTarget is double room)
        {
            if (room <= 0)
            {
                imminent.Add((TimeSpan.Zero, MakeWarning(quota, "Pause non-urgent turns",
                    "You’ve reached your session target.", session.End, session) with { Title = "Codex session target reached" }));
            }
            else if (pace.RecentRate is double rate && double.IsFinite(rate) && rate > 0)
            {
                double hoursToTarget = room / rate;
                double leadHours = Math.Min(NotificationHorizon.TotalHours, (session.End - now).TotalHours);
                if (hoursToTarget <= leadHours)
                {
                    var untilTarget = TimeSpan.FromHours(hoursToTarget);
                    imminent.Add((untilTarget, ForecastWarning(quota, untilTarget, session.End, session)));
                }
            }
        }

        if (pace.RecentRate is > 0 && pace.ExhaustionDate is { } exhaustion
            && exhaustion >= now && exhaustion - now <= NotificationHorizon)
        {
            var untilTarget = exhaustion - now;
            imminent.Add((untilTarget, ForecastWarning(quota, untilTarget, pace.PlanEndsAt)));
        }

        foreach (var candidate in imminent.OrderBy(candidate => candidate.UntilTarget)
                     .ThenBy(candidate => candidate.Warning.Key.StartsWith("weekly.", StringComparison.Ordinal) ? 0 : 1).Take(1))
            yield return candidate.Warning;
    }

    private static BudgetWarning ForecastWarning(
        QuotaSample quota, TimeSpan untilTarget, DateTimeOffset deadline, WorkSession? session = null)
    {
        int minutes = Math.Max(1, (int)Math.Ceiling(untilTarget.TotalMinutes));
        string target = session is null ? "the weekly limit" : "your session target";
        return MakeWarning(quota, "Run fewer turns or pause non-urgent work.",
            $"At this pace, you could reach {target} in about {minutes} min.", deadline, session) with { Title = "Slow down on Codex" };
    }

    public static string ActionLabel(SessionAction action) => action switch
    {
        SessionAction.Pause => "Pause",
        SessionAction.SlowDown => "Slow down",
        SessionAction.KeepPace => "Keep this pace",
        SessionAction.RoomToSpeedUp => "Room to speed up",
        SessionAction.CheckUsage => "Check usage",
        _ => "Checking",
    };

    private static bool HasFreshQuota(QuotaSample? quota, DateTimeOffset now) =>
        quota is not null && quota.UsedPercent is >= 0 and <= 100 && quota.ResetsAt > now
            && quota.ObservedAt <= now && now - quota.ObservedAt <= QuotaFreshness;

    private static Pace CalculatePace(
        QuotaSample quota, IReadOnlyList<QuotaSample> history, Preferences preferences,
        DateTimeOffset planEndsAt, DateTimeOffset now)
    {
        var windowStart = quota.ResetsAt.AddMinutes(-quota.WindowMinutes);
        var schedule = preferences.Schedule.IsValid ? preferences.Schedule : WorkSchedule.Standard;
        var sessions = WorkPlan(windowStart, planEndsAt, schedule, preferences.Workdays);
        double elapsed = WorkSeconds(sessions, windowStart, now);
        double remaining = WorkSeconds(sessions, now, planEndsAt);
        double total = elapsed + remaining;
        double target = total > 0 ? 100 * elapsed / total : 0;
        double remainingHours = remaining / 3600;
        double? safeRate = remainingHours > 0 ? Math.Max(0, 100 - quota.UsedPercent) / remainingHours : null;
        var samples = CurrentSamples(history, quota, windowStart, now);
        double? recentRate = RecentWorkRate(samples, sessions, now);
        double? projection = remainingHours > 0 ? quota.UsedPercent + recentRate * remainingHours : null;
        var session = sessions.FirstOrDefault(candidate => candidate.Start <= now && now < candidate.End);
        double? room = session is null ? null : session.CumulativeTargetPercent - quota.UsedPercent;
        double? projectedRoom = session is null ? null : room - recentRate * (session.End - now).TotalHours;

        double? budget = null;
        double? sessionTarget = null;
        double? sessionUsed = null;
        DateTimeOffset? partialSince = null;
        double? partialUsed = null;
        double? partialTarget = null;
        bool inferred = false;
        if (session is not null)
        {
            var firstInSession = samples.FirstOrDefault(sample => sample.ObservedAt >= session.Start);
            var beforeStart = samples.LastOrDefault(sample => sample.ObservedAt <= session.Start);
            bool nearStart = beforeStart is not null && session.Start - beforeStart.ObservedAt <= TimeSpan.FromMinutes(10);
            inferred = !nearStart && beforeStart is not null && firstInSession?.UsedPercent == beforeStart.UsedPercent;
            if (beforeStart is not null && (nearStart || inferred))
            {
                budget = Math.Max(0, session.CumulativeTargetPercent - beforeStart.UsedPercent);
                sessionTarget = budget * (now - session.Start).TotalSeconds / (session.End - session.Start).TotalSeconds;
                if (firstInSession is not null)
                {
                    sessionUsed = Math.Max(0, samples[^1].UsedPercent - beforeStart.UsedPercent);
                }
            }
            else if (firstInSession is not null)
            {
                partialSince = firstInSession.ObservedAt;
                var latest = samples[^1];
                if (latest.ObservedAt > firstInSession.ObservedAt)
                {
                    partialUsed = Math.Max(0, latest.UsedPercent - firstInSession.UsedPercent);
                }

                double roomAtFirstReading = Math.Max(0, session.CumulativeTargetPercent - firstInSession.UsedPercent);
                partialTarget = roomAtFirstReading * (now - firstInSession.ObservedAt).TotalSeconds /
                    (session.End - firstInSession.ObservedAt).TotalSeconds;
            }
        }

        return new Pace(
            planEndsAt, sessions, session, target, remainingHours, safeRate, recentRate,
            projection, EstimatedExhaustion(sessions, now, quota.UsedPercent, recentRate),
            SessionRoomToTarget: room,
            SessionProjectedRoomToTarget: projectedRoom,
            SessionBudget: budget,
            SessionTarget: sessionTarget,
            SessionUsed: sessionUsed,
            SessionStartInferred: inferred,
            PartialSince: partialSince,
            SessionStatus: EvaluateSession(room, sessionUsed ?? partialUsed, sessionTarget ?? partialTarget));
    }

    private static SessionPaceStatus? EvaluateSession(double? room, double? used, double? target)
    {
        if (room is null) return null;
        if (room <= 0) return SessionPaceStatus.BudgetUsed;
        if (used is null || target is null) return SessionPaceStatus.AwaitingReadings;
        return used > target + 0.5 ? SessionPaceStatus.OverPace : SessionPaceStatus.OnTrack;
    }

    private static SessionGuidance? GuideSession(Pace pace, QuotaSample quota, DateTimeOffset now)
    {
        if (pace.CurrentSession is not { } session || now < session.Start || now >= session.End) return null;
        if (!HasFreshQuota(quota, now))
        {
            return new SessionGuidance(SessionAction.CheckUsage, null, false);
        }

        bool weeklyExhausted = quota.UsedPercent >= 100;
        bool weeklyRisk = weeklyExhausted || pace.ProjectedPercent is >= 100;
        var status = pace.SessionStatus;
        SessionAction action;
        if (weeklyExhausted || status == SessionPaceStatus.BudgetUsed)
        {
            action = SessionAction.Pause;
        }
        else if (weeklyRisk || pace.SessionProjectedRoomToTarget is < -0.75 || status == SessionPaceStatus.OverPace)
        {
            action = SessionAction.SlowDown;
        }
        else if (pace.SessionBudget is double budget && pace.SessionUsed is double used &&
            pace.SessionTarget is double target && target - used >= Math.Max(0.5, budget * 0.10))
        {
            action = SessionAction.RoomToSpeedUp;
        }
        else
        {
            action = status == SessionPaceStatus.OnTrack ? SessionAction.KeepPace : SessionAction.AwaitingReadings;
        }

        double? safeSessionRate = null;
        if (action != SessionAction.Pause && pace.SessionBudget is double allowance && allowance > 0 &&
            pace.SessionUsed is not null && pace.SessionRoomToTarget is double room && room > 0)
        {
            double rate = room / (session.End - now).TotalHours;
            safeSessionRate = Math.Min(rate, pace.SafeRate ?? rate) * 100 / allowance;
        }

        return new SessionGuidance(action, safeSessionRate, weeklyRisk);
    }

    private static WorkSession[] WorkPlan(
        DateTimeOffset start, DateTimeOffset end, WorkSchedule schedule, IReadOnlyList<DayOfWeek> workdays)
    {
        if (start >= end || workdays.Count == 0) return [];
        var selectedDays = workdays.ToHashSet();
        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End, SessionTemplate Template)>();

        for (var day = WorkClock.Day(start); WorkClock.At(day, 0) < end; day = day.AddDays(1))
        {
            if (!selectedDays.Contains(day.DayOfWeek)) continue;

            foreach (var template in schedule.EntriesOn(day.DayOfWeek).OrderBy(template => template.StartMinute))
            {
                var shiftStart = Later(start, WorkClock.At(day, template.StartMinute));
                var shiftEnd = Earlier(end, WorkClock.At(day, template.EndMinute));
                if (shiftStart < shiftEnd) intervals.Add((shiftStart, shiftEnd, template));
            }
        }

        double duration = intervals.Sum(interval => (interval.End - interval.Start).TotalSeconds);
        double scheduledThroughEnd = 0;
        var sessions = new List<WorkSession>(intervals.Count);
        foreach (var interval in intervals)
        {
            double seconds = (interval.End - interval.Start).TotalSeconds;
            scheduledThroughEnd += seconds;
            sessions.Add(new WorkSession(interval.Start, interval.End,
                100 * seconds / duration, 100 * scheduledThroughEnd / duration, interval.Template));
        }

        return sessions.ToArray();
    }

    private static double WorkSeconds(IReadOnlyList<WorkSession> sessions, DateTimeOffset start, DateTimeOffset end)
    {
        double seconds = 0;
        foreach (var session in sessions)
        {
            seconds += Math.Max(0, (Earlier(end, session.End) - Later(start, session.Start)).TotalSeconds);
        }

        return seconds;
    }

    private static double? RecentWorkRate(QuotaSample[] samples, IReadOnlyList<WorkSession> sessions, DateTimeOffset now)
    {
        if (samples.Length < 2 || WorkSeconds(sessions, samples[^1].ObservedAt, now) > 1800) return null;
        const double Horizon = 4 * 3600;
        double secondsObserved = 0;
        double usageObserved = 0;
        for (int index = samples.Length - 1; index > 0; index--)
        {
            var earlier = samples[index - 1];
            var later = samples[index];
            double secondsSince = WorkSeconds(sessions, later.ObservedAt, now);
            if (secondsSince >= Horizon) break;
            double duration = (later.ObservedAt - earlier.ObservedAt).TotalSeconds;
            double scheduled = WorkSeconds(sessions, earlier.ObservedAt, later.ObservedAt);
            double increase = later.UsedPercent - earlier.UsedPercent;
            // An increase across off-hours cannot be attributed to a scheduled shift.
            if (increase != 0 && duration - scheduled >= 1) continue;
            secondsObserved += Math.Min(scheduled, Horizon - secondsSince);
            if (scheduled > 0) usageObserved += increase * Math.Min(1, (Horizon - secondsSince) / scheduled);
        }

        return secondsObserved >= 1800 ? usageObserved * 3600 / secondsObserved : null;
    }

    private static DateTimeOffset? EstimatedExhaustion(
        IReadOnlyList<WorkSession> sessions, DateTimeOffset now, double used, double? recentRate)
    {
        if (used >= 100) return now;
        if (recentRate is not double rate || rate <= 0) return null;

        double secondsNeeded = (100 - used) * 3600 / rate;
        foreach (var session in sessions)
        {
            if (session.End <= now) continue;
            var start = Later(now, session.Start);
            double duration = (session.End - start).TotalSeconds;
            if (secondsNeeded <= duration) return start.AddSeconds(secondsNeeded);
            secondsNeeded -= duration;
        }

        return null;
    }

    private static (string Status, string Recommendation, string Detail, BudgetWarning? Warning, bool Show) Recommend(
        QuotaSample? quota, Pace? pace, SessionGuidance? guidance, DateTimeOffset? deadline, DateTimeOffset now)
    {
        if (quota is null)
        {
            return ("Waiting for quota", "Waiting for Codex usage",
                "Once Codex reports your weekly allowance, your schedule and pace will appear here.", null, false);
        }

        if (quota.ResetsAt <= now)
        {
            return ("Waiting for reset data", "Refresh after the reset",
                "The last confirmed weekly reading belongs to the previous window.", null, false);
        }

        if (!HasFreshQuota(quota, now))
        {
            return ("Quota reading is stale", "Refresh to check your pace",
                "Open or continue a Codex task as usual if a live account reading is unavailable.", null, false);
        }

        if (quota.UsedPercent >= 100)
        {
            var reset = WorkClock.Local(quota.ResetsAt).ToString("ddd, d MMM HH:mm", CultureInfo.CurrentCulture);
            return ("Weekly limit reached", $"Weekly quota resets {reset}",
                "The latest account reading shows the weekly allowance at 100%.", null, true);
        }

        if (pace?.CurrentSession is { } active && guidance?.Action == SessionAction.Pause)
        {
            const string recommendation = "Pause non-urgent turns until your next session";
            const string detail = "You’ve reached your session target.";
            return ("Session budget used", recommendation, detail,
                MakeWarning(quota, recommendation, detail, active.End, active), true);
        }

        if (pace?.ProjectedPercent is >= 100)
        {
            const string recommendation = "Use Codex for urgent work only";
            var when = deadline.HasValue ? "your reset target" : "reset";
            var detail = SessionUsageDetail(pace) ?? $"At this pace, your weekly quota could run out before {when}.";
            return ("Slow down", recommendation, detail, MakeWarning(quota, recommendation, detail, pace.PlanEndsAt), true);
        }

        if (pace?.CurrentSession is { } session && guidance?.Action == SessionAction.SlowDown)
        {
            const string recommendation = "Run fewer turns";
            var forecast = pace.SessionProjectedRoomToTarget is < -0.75
                ? "At this pace, you could reach your session target before the session ends." : null;
            var detail = string.Join(" ", new[] { SessionUsageDetail(pace), forecast }.OfType<string>());
            return ("Slow down", recommendation, detail, MakeWarning(quota, recommendation, detail, session.End, session), true);
        }

        if (pace?.RemainingWorkHours == 0)
        {
            return ("Workweek complete", deadline.HasValue
                    ? "No more work scheduled before your target" : "No scheduled work before reset",
                "Any extra Codex activity will use the allowance shown above.", null, false);
        }

        if (pace is not null && (pace.RecentRate is not null || guidance?.Action is SessionAction.KeepPace or SessionAction.RoomToSpeedUp))
        {
            bool ahead = quota.UsedPercent > pace.TargetPercent + 2;
            return (ahead ? "Pace is sustainable" : "On pace", "Keep this pace", ahead
                ? "Usage is above the schedule target, and your recent pace fits the remaining allowance."
                : "Usage fits your current pacing target.", null, true);
        }

        return ("Learning your pace", "Waiting for more usage readings",
            "The schedule target updates throughout the day. Forecasts need at least 30 minutes of observed work time.", null, false);
    }

    private static string? SessionUsageDetail(Pace pace)
    {
        if (pace.SessionBudget is not > 0 || pace.SessionUsed is not double used || pace.SessionTarget is not double target)
            return null;

        var budget = pace.SessionBudget.Value;
        return $"{QuotaDisplay.Percent(used / budget * 100)} of your session budget used. " +
            $"Target by now is {QuotaDisplay.Percent(target / budget * 100)}.";
    }

    private static BudgetWarning MakeWarning(
        QuotaSample quota, string recommendation, string detail, DateTimeOffset deadline, WorkSession? session = null)
    {
        long reset = quota.ResetsAt.ToUnixTimeMilliseconds();
        var expiresAt = Earlier(quota.ResetsAt, deadline);
        if (session is null)
        {
            return new BudgetWarning(FormattableString.Invariant($"weekly.{reset}"),
                "Weekly Codex budget at risk", recommendation, detail, expiresAt);
        }

        long day = WorkClock.At(WorkClock.Day(session.Start), 0).ToUnixTimeMilliseconds();
        Guid templateId = session.Template.Id;
        string sessionKey = templateId == WorkSchedule.MorningId ? "morning"
            : templateId == WorkSchedule.AfternoonId ? "afternoon" : templateId.ToString("D");
        return new BudgetWarning(FormattableString.Invariant($"session.{reset}.{day}.{sessionKey}"),
            "Codex session budget at risk", recommendation, detail, expiresAt);
    }

    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) =>
        first <= second ? first : second;

    private static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) =>
        first >= second ? first : second;

    private static string EffortName(string effort, string? model)
    {
        if (effort == "low" && model?.EndsWith("-astra", StringComparison.Ordinal) == true) return "Light";
        if (effort == "xhigh") return "Extra high";
        return effort.Length == 0 ? effort : char.ToUpperInvariant(effort[0]) + effort[1..];
    }

    private static string ModelName(string id, IReadOnlyList<ModelOption> models) =>
        models.FirstOrDefault(model => model.Id == id)?.Name ?? id;

    private static string ActivityCaption(Activity? activity, IReadOnlyList<ModelOption> models)
    {
        if (activity is null) return "No recent local Codex activity";

        string model = activity.Model is string id ? ModelName(id, models) : "Model unavailable";
        string effort = activity.Effort is string value ? $" · {EffortName(value, activity.Model)}" : "";
        string turns = activity.Turns == 1 ? "1 turn" : $"{activity.Turns} turns";
        return $"{model}{effort} · {turns} in the last hour";
    }

    private static QuotaSample[] CurrentSamples(
        IReadOnlyList<QuotaSample> history, QuotaSample quota, DateTimeOffset windowStart, DateTimeOffset now)
    {
        var matching = new Dictionary<DateTimeOffset, QuotaSample>();
        var observedThrough = Earlier(quota.ObservedAt, now);
        foreach (var sample in history.Append(quota))
        {
            if (sample.ResetsAt == quota.ResetsAt && sample.WindowMinutes == quota.WindowMinutes &&
                sample.ObservedAt >= windowStart && sample.ObservedAt <= observedThrough &&
                sample.UsedPercent is >= 0 and <= 100)
            {
                matching[sample.ObservedAt] = sample;
            }
        }

        var ordered = matching.Values.OrderBy(sample => sample.ObservedAt).ToArray();
        if (ordered.Length == 0) return [];

        int startIndex = 0;
        double peak = ordered[0].UsedPercent;
        for (int index = 1; index < ordered.Length; index++)
        {
            double used = ordered[index].UsedPercent;
            // A one-point reversal can come from rounding or different reporting sources.
            if (peak - used > 1)
            {
                startIndex = index;
                peak = used;
            }
            else
            {
                peak = Math.Max(peak, used);
            }
        }

        double latest = ordered[^1].UsedPercent;
        double normalized = 0;
        for (int index = startIndex; index < ordered.Length; index++)
        {
            normalized = Math.Max(normalized, Math.Min(ordered[index].UsedPercent, latest));
            ordered[index] = ordered[index] with { UsedPercent = normalized };
        }

        return ordered[startIndex..];
    }
}
