namespace LibreFlow.Core.Storage;

public sealed record DictationRow(DateTime LocalTime, int Words, double Seconds, string? App);
public sealed record DayPoint(DateTime Date, int Words);
public sealed record AppStat(string App, int Words, int Count);

public sealed record InsightsResult(
    long TotalWords,
    double TotalSeconds,
    int Dictations,
    double AverageWpm,
    double MinutesSaved,
    double MinutesSavedThisWeek,
    int DayStreak,
    int WeekStreak,
    long WordsToday,
    long WordsThisWeek,
    IReadOnlyList<DayPoint> Daily,
    IReadOnlyList<AppStat> TopApps);

public static class InsightsCalculator
{
    private const int SpeedSampleSize = 100;

    public static InsightsResult Compute(IReadOnlyList<DictationRow> rows, DateTime now, double typingWpm, int days = 30)
    {
        var today = now.Date;
        var weekStart = WeekStart(today);

        long totalWords = 0, wordsToday = 0, wordsWeek = 0;
        double totalSeconds = 0, secondsWeek = 0;
        var activeDays = new HashSet<DateTime>();
        var perDay = new Dictionary<DateTime, int>();
        var perApp = new Dictionary<string, (int Words, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rows)
        {
            var date = r.LocalTime.Date;
            totalWords += r.Words;
            totalSeconds += r.Seconds;
            if (r.Words > 0) activeDays.Add(date);
            perDay[date] = perDay.GetValueOrDefault(date) + r.Words;
            if (date == today) wordsToday += r.Words;
            if (date >= weekStart)
            {
                wordsWeek += r.Words;
                secondsWeek += r.Seconds;
            }
            if (!string.IsNullOrWhiteSpace(r.App))
            {
                var cur = perApp.GetValueOrDefault(r.App);
                perApp[r.App] = (cur.Words + r.Words, cur.Count + 1);
            }
        }

        long sampleWords = 0;
        double sampleSeconds = 0;
        foreach (var r in rows.Where(r => r.Words > 0 && r.Seconds > 0).OrderByDescending(r => r.LocalTime).Take(SpeedSampleSize))
        {
            sampleWords += r.Words;
            sampleSeconds += r.Seconds;
        }

        var daily = new List<DayPoint>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            daily.Add(new DayPoint(d, perDay.GetValueOrDefault(d)));
        }

        return new InsightsResult(
            totalWords,
            totalSeconds,
            rows.Count,
            sampleSeconds > 0 ? sampleWords / (sampleSeconds / 60.0) : 0,
            Saved(totalWords, totalSeconds, typingWpm),
            Saved(wordsWeek, secondsWeek, typingWpm),
            Streak(today, d => activeDays.Contains(d), d => d.AddDays(-1)),
            Streak(weekStart, w => HasActivityInWeek(activeDays, w), w => w.AddDays(-7)),
            wordsToday,
            wordsWeek,
            daily,
            perApp.Select(p => new AppStat(p.Key, p.Value.Words, p.Value.Count))
                .OrderByDescending(a => a.Words).Take(8).ToList());
    }

    private static double Saved(long words, double seconds, double typingWpm) =>
        typingWpm <= 0 ? 0 : Math.Max(0, words / typingWpm - seconds / 60.0);

    public static DateTime WeekStart(DateTime date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static bool HasActivityInWeek(HashSet<DateTime> activeDays, DateTime weekStart)
    {
        for (var i = 0; i < 7; i++) if (activeDays.Contains(weekStart.AddDays(i))) return true;
        return false;
    }

    /// <summary>A streak is still alive if the current period is empty but the previous one was not.</summary>
    private static int Streak(DateTime current, Func<DateTime, bool> active, Func<DateTime, DateTime> previous)
    {
        var cursor = active(current) ? current : previous(current);
        var n = 0;
        while (active(cursor))
        {
            n++;
            cursor = previous(cursor);
        }
        return n;
    }
}
