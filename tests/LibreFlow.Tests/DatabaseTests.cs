using LibreFlow.Core.Storage;

namespace LibreFlow.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ow-test-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly Database _db;

    public DatabaseTests() => _db = new Database(_path);

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void History_round_trips_and_lists_newest_first()
    {
        var t = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        _db.AddHistory(new HistoryEntry { Timestamp = t, RawText = "uno", Text = "Uno.", Words = 1, Duration = 2.5, App = "chrome" });
        _db.AddHistory(new HistoryEntry { Timestamp = t.AddMinutes(1), RawText = "dos", Text = "Dos.", Words = 1, Duration = 1, App = "whatsapp" });

        var list = _db.GetHistory(10, 0);

        Assert.Equal(["Dos.", "Uno."], list.Select(h => h.Text));
        Assert.Equal(t, list[1].Timestamp);
        Assert.Equal(2.5, list[1].Duration);
    }

    [Fact]
    public void History_search_matches_text_and_app()
    {
        _db.AddHistory(new HistoryEntry { Text = "Mandame el presupuesto", RawText = "x", App = "whatsapp" });
        _db.AddHistory(new HistoryEntry { Text = "Otra cosa", RawText = "x", App = "chrome" });

        Assert.Single(_db.GetHistory(10, 0, "presupuesto"));
        Assert.Single(_db.GetHistory(10, 0, "chrome"));
        Assert.Equal(1, _db.CountHistory("presupuesto"));
    }

    [Fact]
    public void Failed_dictations_do_not_count_for_insights()
    {
        _db.AddHistory(new HistoryEntry { Text = "a b c", RawText = "a b c", Words = 3, Duration = 2, Status = "ok" });
        _db.AddHistory(new HistoryEntry { Text = "", RawText = "", Words = 0, Duration = 9, Status = "failed" });

        Assert.Equal(3, _db.GetDictationRows().Sum(r => r.Words));
        Assert.Single(_db.GetDictationRows());
    }

    [Fact]
    public void Saving_the_same_dictionary_word_twice_updates_it()
    {
        _db.SaveDictionaryEntry(0, "Vercel", null, false);
        _db.SaveDictionaryEntry(0, "vercel", "bercel", true);

        var entry = Assert.Single(_db.GetDictionary());
        Assert.Equal("bercel", entry.ReplaceFrom);
        Assert.True(entry.Starred);
    }

    [Fact]
    public void Snippets_can_be_edited_and_deleted()
    {
        _db.SaveSnippet(0, "mi mail", "a@b.com");
        var id = _db.GetSnippets().Single().Id;
        _db.SaveSnippet(id, "mi mail", "c@d.com");
        Assert.Equal("c@d.com", _db.GetSnippets().Single().Expansion);
        _db.DeleteSnippet(id);
        Assert.Empty(_db.GetSnippets());
    }

    [Fact]
    public void Correction_stats_exclude_undone_and_rank_repeated_typos()
    {
        _db.AddCorrection("qeu", "que", "Typo", "chrome");
        _db.AddCorrection("qeu", "que", "Typo", "chrome");
        _db.AddCorrection("ohla", "hola", "Typo", "chrome");
        _db.MarkCorrectionUndone("ohla", "hola");

        var stats = _db.GetCorrectionStats(DateTime.Now);

        Assert.Equal(2, stats.Total);
        Assert.Equal(1, stats.Undone);
        Assert.Equal(new CorrectionPair("qeu", "que", 2), stats.Top[0]);
    }

    [Fact]
    public void Learned_words_are_stored_once()
    {
        _db.AddLearned("Groq");
        _db.AddLearned("groq");
        Assert.Equal(["groq"], _db.GetLearned());
    }
}
