using LibreFlow.Core.Storage;

namespace LibreFlow.Tests;

public class InsightsTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 15, 0, 0, DateTimeKind.Local); // a Friday

    private static DictationRow Row(int daysAgo, int words, double seconds, string app = "chrome") =>
        new(Now.AddDays(-daysAgo), words, seconds, app);

    [Fact]
    public void Speed_is_words_over_minutes_of_speech()
    {
        var r = InsightsCalculator.Compute([Row(0, 150, 60), Row(1, 90, 60)], Now, typingWpm: 40);
        Assert.Equal(120, r.AverageWpm, 3);
    }

    [Fact]
    public void Time_saved_is_typing_time_minus_speaking_time()
    {
        // 400 words at 40 wpm would take 10 minutes to type; saying them took 3.
        var r = InsightsCalculator.Compute([Row(0, 400, 180)], Now, typingWpm: 40);
        Assert.Equal(7, r.MinutesSaved, 3);
    }

    [Fact]
    public void Time_saved_is_never_negative()
    {
        var r = InsightsCalculator.Compute([Row(0, 5, 600)], Now, typingWpm: 40);
        Assert.Equal(0, r.MinutesSaved);
    }

    [Fact]
    public void Day_streak_counts_consecutive_days_up_to_today()
    {
        var r = InsightsCalculator.Compute([Row(0, 10, 5), Row(1, 10, 5), Row(2, 10, 5), Row(4, 10, 5)], Now, 40);
        Assert.Equal(3, r.DayStreak);
    }

    [Fact]
    public void Day_streak_survives_until_the_end_of_the_next_day()
    {
        var r = InsightsCalculator.Compute([Row(1, 10, 5), Row(2, 10, 5)], Now, 40);
        Assert.Equal(2, r.DayStreak);
    }

    [Fact]
    public void Day_streak_is_zero_after_a_missed_day()
    {
        var r = InsightsCalculator.Compute([Row(2, 10, 5), Row(3, 10, 5)], Now, 40);
        Assert.Equal(0, r.DayStreak);
    }

    [Fact]
    public void Totals_split_today_and_this_week_starting_monday()
    {
        // Now is Friday: 4 days ago is Monday (this week), 5 days ago is Sunday (last week).
        var r = InsightsCalculator.Compute([Row(0, 10, 5), Row(4, 20, 5), Row(5, 40, 5)], Now, 40);
        Assert.Equal(70, r.TotalWords);
        Assert.Equal(10, r.WordsToday);
        Assert.Equal(30, r.WordsThisWeek);
    }

    [Fact]
    public void Week_streak_counts_consecutive_weeks_with_activity()
    {
        var r = InsightsCalculator.Compute([Row(0, 10, 5), Row(7, 10, 5), Row(14, 10, 5), Row(35, 10, 5)], Now, 40);
        Assert.Equal(3, r.WeekStreak);
    }

    [Fact]
    public void Top_apps_are_ranked_by_words()
    {
        var r = InsightsCalculator.Compute([Row(0, 10, 5, "chrome"), Row(0, 50, 5, "whatsapp"), Row(1, 15, 5, "chrome")], Now, 40);
        Assert.Equal(["whatsapp", "chrome"], r.TopApps.Select(a => a.App));
        Assert.Equal(25, r.TopApps[1].Words);
    }

    [Fact]
    public void Daily_series_covers_every_day_including_empty_ones()
    {
        var r = InsightsCalculator.Compute([Row(0, 10, 5), Row(2, 30, 5)], Now, 40, days: 3);
        Assert.Equal([30, 0, 10], r.Daily.Select(d => d.Words));
        Assert.Equal(Now.Date, r.Daily[^1].Date);
    }
}
