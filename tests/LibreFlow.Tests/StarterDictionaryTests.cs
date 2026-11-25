using LibreFlow.Core.Storage;

namespace LibreFlow.Tests;

public sealed class StarterWordsDatabaseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ow-test-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly Database _db;

    public StarterWordsDatabaseTests() => _db = new Database(_path);

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void A_new_database_gets_the_starter_words_as_suggestions()
    {
        _db.AddStarterWords();

        var words = _db.GetDictionary();
        Assert.Equal(StarterDictionary.Words.Count, words.Count);
        var claude = Assert.Single(words, w => w.Phrase == "Claude Code");
        Assert.Equal("sugerida", claude.Source);
        Assert.Contains("cloud code", claude.ReplaceFrom);
    }

    [Fact]
    public void Starter_words_are_added_once_so_a_deleted_one_stays_deleted()
    {
        _db.AddStarterWords();
        var vercel = _db.GetDictionary().Single(w => w.Phrase == "Vercel");
        _db.DeleteDictionaryEntry(vercel.Id);

        _db.AddStarterWords();

        Assert.DoesNotContain(_db.GetDictionary(), w => w.Phrase == "Vercel");
        Assert.Equal(StarterDictionary.Words.Count - 1, _db.GetDictionary().Count);
    }

    [Fact]
    public void A_word_the_user_already_had_keeps_the_users_version()
    {
        _db.SaveDictionaryEntry(0, "vercel", "berzel", true);

        _db.AddStarterWords();

        var entry = Assert.Single(_db.GetDictionary(), w => string.Equals(w.Phrase, "vercel", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("vercel", entry.Phrase);
        Assert.Equal("berzel", entry.ReplaceFrom);
        Assert.True(entry.Starred);
        Assert.Equal("manual", entry.Source);
    }

    [Fact]
    public void The_users_own_words_come_before_the_suggested_ones()
    {
        // The speech model only gets the first words of the list as a hint, so the user's must lead.
        _db.AddStarterWords();
        _db.SaveDictionaryEntry(0, "Zapatería Ñandú", null, false);
        _db.SaveDictionaryEntry(0, "Xilófono", null, true);

        var phrases = _db.GetDictionary().Select(w => w.Phrase).ToList();

        Assert.Equal("Xilófono", phrases[0]);
        Assert.Equal("Zapatería Ñandú", phrases[1]);
        Assert.Equal(StarterDictionary.Words[0].Phrase, phrases[2]);
        Assert.Equal(StarterDictionary.Words[1].Phrase, phrases[3]);
    }
}

[Collection("lexicon")]
public class StarterDictionaryTests(LexiconFixture fx)
{
    [Fact]
    public void No_word_is_listed_twice()
    {
        var repeated = StarterDictionary.Words.GroupBy(w => w.Phrase, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(repeated);
    }

    [Fact]
    public void A_misheard_form_never_belongs_to_two_words_or_equals_a_listed_word()
    {
        var forms = StarterDictionary.Words.SelectMany(w => Forms(w.ReplaceFrom)).ToList();

        Assert.Empty(forms.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key));
        var phrases = StarterDictionary.Words.Select(w => w.Phrase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(forms.Where(phrases.Contains));
    }

    [Fact]
    public void A_single_word_misheard_form_is_never_a_real_word()
    {
        // Replacing a real word would silently change what people actually said.
        var real = StarterDictionary.Words
            .SelectMany(w => Forms(w.ReplaceFrom))
            .Where(f => !f.Contains(' ') && fx.Lexicon.IsDictionaryWord(f.ToLowerInvariant()));
        Assert.Empty(real);
    }

    private static IEnumerable<string> Forms(string? replaceFrom) =>
        (replaceFrom ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
