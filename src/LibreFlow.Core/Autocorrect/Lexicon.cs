using WeCantSpell.Hunspell;

namespace LibreFlow.Core.Autocorrect;

/// <summary>Spanish + English word knowledge: validity (Hunspell), frequency and accent-folded lookup.</summary>
public sealed class Lexicon
{
    public readonly record struct Entry(string Word, double LogFreq, bool English);

    public const double UnknownFreq = -7.5;

    private readonly WordList _es;
    private readonly WordList _en;
    private readonly Dictionary<string, Entry[]> _byFold = new();
    private readonly Dictionary<string, double> _freq = new();
    private readonly Dictionary<string, double> _enFreq = new();
    private readonly HashSet<string> _allow = new();

    private static readonly string[] Clitics =
    [
        "melos", "melas", "telos", "telas", "selos", "selas", "noslo", "nosla",
        "melo", "mela", "telo", "tela", "selo", "sela",
        "nos", "los", "las", "les", "me", "te", "lo", "la", "le", "se",
    ];

    private Lexicon(WordList es, WordList en)
    {
        _es = es;
        _en = en;
    }

    public static Lexicon Load(string dataDir)
    {
        var es = WordList.CreateFromFiles(Path.Combine(dataDir, "es_AR.dic"), Path.Combine(dataDir, "es_AR.aff"));
        var en = WordList.CreateFromFiles(Path.Combine(dataDir, "en_US.dic"), Path.Combine(dataDir, "en_US.aff"));
        var lex = new Lexicon(es, en);

        var allowPath = Path.Combine(dataDir, "allow.txt");
        if (File.Exists(allowPath))
        {
            foreach (var line in File.ReadLines(allowPath))
            {
                if (line.StartsWith('#')) continue;
                foreach (var w in line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    lex._allow.Add(w.ToLowerInvariant());
            }
        }

        var folded = new Dictionary<string, List<Entry>>();
        lex.LoadFrequencies(Path.Combine(dataDir, "es_50k.txt"), english: false, folded);
        lex.LoadFrequencies(Path.Combine(dataDir, "en_50k.txt"), english: true, folded);
        foreach (var (key, list) in folded) lex._byFold[key] = list.ToArray();
        return lex;
    }

    private void LoadFrequencies(string path, bool english, Dictionary<string, List<Entry>> folded)
    {
        var rows = new List<(string Word, long Count)>(50_000);
        long total = 0;
        foreach (var line in File.ReadLines(path))
        {
            var sp = line.IndexOf(' ');
            if (sp <= 0 || !long.TryParse(line.AsSpan(sp + 1), out var count)) continue;
            total += count;
            rows.Add((line[..sp], count));
        }

        foreach (var (word, count) in rows)
        {
            if (!TextUtil.IsAllLetters(word) || word != word.ToLowerInvariant()) continue;
            if (_freq.ContainsKey(word)) continue;
            var logFreq = Math.Log10((double)count / total);
            if (english) _enFreq[word] = logFreq;
            var ok = english
                ? (word.Length > 1 || word is "a" or "i") && IsEnglish(word)
                : IsSpanish(word);
            if (!ok) continue;

            _freq[word] = logFreq;
            var key = TextUtil.Fold(word);
            if (!folded.TryGetValue(key, out var list)) folded[key] = list = new List<Entry>(1);
            list.Add(new Entry(word, logFreq, english));
        }
    }

    /// <summary>Real dictionary word (Spanish incl. voseo, or English). Excludes the slang/tech allowlist.</summary>
    public bool IsDictionaryWord(string lower) => IsSpanish(lower) || IsEnglish(lower);

    /// <summary>
    /// English is the second language here, so its short obscure words ("tam", "manana")
    /// are not trusted: they are far more likely to be Spanish typos.
    /// </summary>
    public bool IsEnglish(string lower) =>
        _en.Check(lower) && (lower.Length >= 8 || (_enFreq.TryGetValue(lower, out var f) && (f >= -5.5 || lower.Length >= 5)));

    /// <summary>
    /// "tenes" → "tenés": an unaccented voseo form that is rarer than its accented twin.
    /// Common words such as "estas" or "sabes" are left alone because both readings are real.
    /// </summary>
    public string? VoseoAccentTwin(string lower)
    {
        if (lower.Length < 4 || lower[^1] != 's') return null;
        var accented = lower[^2] switch { 'a' => 'á', 'e' => 'é', 'i' => 'í', _ => '\0' };
        if (accented == '\0') return null;
        var twin = string.Concat(lower.AsSpan(0, lower.Length - 2), new ReadOnlySpan<char>(in accented), "s");
        if (!IsVoseoBase(twin, accented: true)) return null;
        var own = LogFreq(lower);
        return own < -4.5 && LogFreq(twin) > own ? twin : null;
    }

    /// <summary>Anything we must not "fix": dictionary words plus the allowlist.</summary>
    public bool IsKnown(string lower) => _allow.Contains(lower) || IsDictionaryWord(lower);

    public bool IsAllowListed(string lower) => _allow.Contains(lower);

    /// <summary>Checks a word with its original casing (proper nouns such as "Argentina").</summary>
    public bool IsKnownAsTyped(string word) => _es.Check(word) || _en.Check(word);

    public bool IsSpanish(string lower) => _es.Check(lower) || IsVoseo(lower);

    public double LogFreq(string lower) => _freq.TryGetValue(lower, out var f) ? f : UnknownFreq;

    public bool TryGetByFold(string foldedKey, out Entry[] entries) => _byFold.TryGetValue(foldedKey, out entries!);

    /// <summary>Rioplatense voseo that Hunspell does not know: "tenés", "decime", "averigualo", "vení".</summary>
    public bool IsVoseo(string w)
    {
        if (w.Length < 3) return false;
        if (IsVoseoBase(w, accented: true)) return true;
        foreach (var clitic in Clitics)
        {
            if (w.Length <= clitic.Length + 1 || !w.EndsWith(clitic, StringComparison.Ordinal)) continue;
            var stem = w[..^clitic.Length];
            // One clitic drops the accent ("decime"), two keep it ("decímelo").
            if (IsVoseoBase(stem, accented: clitic.Length > 3) || IsVoseoBase(stem, accented: false)) return true;
        }
        return false;
    }

    private bool IsVoseoBase(string w, bool accented)
    {
        if (w.Length < 2) return false;
        var last = w[^1];
        if (accented)
        {
            // Present: tenés, hablás, venís.
            if (w.Length >= 4 && last == 's')
            {
                var inf = w[^2] switch { 'á' => "ar", 'é' => "er", 'í' => "ir", _ => null };
                if (inf != null && _es.Check(w[..^2] + inf)) return true;
            }
            // Imperative: mirá, poné, vení.
            var imp = last switch { 'á' => "ar", 'é' => "er", 'í' => "ir", _ => null };
            return imp != null && _es.Check(w[..^1] + imp);
        }
        var plain = last switch { 'a' => "ar", 'e' => "er", 'i' => "ir", _ => null };
        return plain != null && w.Length >= 3 && _es.Check(w[..^1] + plain);
    }
}
