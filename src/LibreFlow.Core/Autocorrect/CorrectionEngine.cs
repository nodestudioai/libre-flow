namespace LibreFlow.Core.Autocorrect;

public sealed class AutocorrectOptions
{
    public bool Typos { get; set; } = true;
    public bool Accents { get; set; } = true;
    public bool Capitalize { get; set; } = true;
    public bool CorrectOnEnter { get; set; } = true;
}

/// <summary>
/// Decides whether a just-typed word should be replaced. Conservative by design:
/// a valid word is never changed (other than sentence-start capitalization), and an
/// unknown word is only changed when one candidate clearly beats the rest.
/// </summary>
public sealed class CorrectionEngine
{
    private const string Alphabet = "abcdefghijklmnñopqrstuvwxyz";
    private const double CostWeight = 2.2;
    private const double MinScore = -9.0;
    private const double MinMargin = 0.35;
    private const double ShortWordMargin = 1.0;
    private const double MaxCost = 1.85;
    private const double MinCandidateFreq = -6.3;
    private const double EnglishPenalty = 1.0;
    private const double SpaceCost = 0.6;
    private const double AccentCost = 0.15;
    private const double ShortWordMinFreq = -3.6;
    private const int UndosToLearn = 2;

    private static readonly Dictionary<char, (double X, int Row)> KeyPos = BuildKeyPositions();
    private static readonly HashSet<(char, char)> Confusable = BuildConfusable();

    private readonly Lexicon _lex;
    private volatile HashSet<string> _userWords = new();
    private volatile HashSet<string> _learned = new();
    private readonly HashSet<string> _sessionIgnore = new();
    private readonly Dictionary<string, int> _undoCounts = new();
    private readonly object _gate = new();

    public CorrectionEngine(Lexicon lexicon) => _lex = lexicon;

    public AutocorrectOptions Options { get; } = new();

    /// <summary>Raised when the user has undone the same correction enough times to keep the word for good.</summary>
    public event Action<string>? WordLearned;

    public void SetUserWords(IEnumerable<string> words) => _userWords = Normalize(words);

    public void SetLearnedWords(IEnumerable<string> words) => _learned = Normalize(words);

    private static HashSet<string> Normalize(IEnumerable<string> words)
    {
        var set = new HashSet<string>();
        foreach (var entry in words)
            foreach (var w in entry.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                set.Add(w.Trim().ToLowerInvariant());
        return set;
    }

    public void RegisterUndo(string original)
    {
        var lower = original.ToLowerInvariant();
        bool learned;
        lock (_gate)
        {
            _sessionIgnore.Add(lower);
            _undoCounts[lower] = _undoCounts.GetValueOrDefault(lower) + 1;
            learned = _undoCounts[lower] == UndosToLearn;
        }
        if (!learned) return;
        var next = new HashSet<string>(_learned) { lower };
        _learned = next;
        WordLearned?.Invoke(lower);
    }

    public void ForgetSessionIgnores()
    {
        lock (_gate) _sessionIgnore.Clear();
    }

    private bool IsProtected(string lower)
    {
        if (_userWords.Contains(lower) || _learned.Contains(lower)) return true;
        lock (_gate) return _sessionIgnore.Contains(lower);
    }

    private bool IsValid(string lower) => IsProtected(lower) || _lex.IsKnown(lower);

    /// <summary>Returns the replacement for a single typed word, or null to leave it alone.</summary>
    public string? CorrectWord(string word, bool sentenceStart)
    {
        if (word.Length is 0 or > 28 || !TextUtil.IsAllLetters(word)) return null;
        var casing = TextUtil.GetCasing(word);
        if (casing == Casing.Other) return null;

        var lower = word.ToLowerInvariant();
        var capitalize = Options.Capitalize && sentenceStart;

        if (IsValid(lower) || (casing == Casing.Capitalized && _lex.IsKnownAsTyped(word)))
        {
            var isProtected = IsProtected(lower) || _lex.IsAllowListed(lower);
            if (Options.Accents && !isProtected && _lex.VoseoAccentTwin(lower) is { } twin)
                return casing == Casing.Capitalized || capitalize ? TextUtil.Capitalize(twin) : twin;
            var plainWord = casing == Casing.Lower && !isProtected && _lex.IsDictionaryWord(lower);
            return capitalize && plainWord ? TextUtil.Capitalize(word) : null;
        }

        // An unknown capitalized word in the middle of a sentence is a name.
        if (casing == Casing.Capitalized && !sentenceStart) return null;
        if (LooksIntentional(lower)) return null;

        var text = Options.Accents ? _lex.VoseoAccentTwin(lower) : null;
        text ??= FindFix(lower)?.Text;
        if (text is null) return null;
        return casing == Casing.Capitalized || capitalize ? TextUtil.Capitalize(text) : text;
    }

    /// <summary>
    /// Repairs a space typed one key early/late or in the middle of a word:
    /// "hol acomo" → "hola como", "ho la" → "hola". Returns the text that replaces
    /// both words, or null when the words are better explained on their own.
    /// </summary>
    public string? CorrectPair(string previous, string current, bool sentenceStart)
    {
        if (!Options.Typos) return null;
        if (!TextUtil.IsAllLetters(previous) || !TextUtil.IsAllLetters(current)) return null;
        var prevCasing = TextUtil.GetCasing(previous);
        if (prevCasing == Casing.Other || TextUtil.GetCasing(current) != Casing.Lower) return null;

        var p = previous.ToLowerInvariant();
        var c = current;
        var pValid = IsValid(p);
        var cValid = IsValid(c);
        if (pValid && cValid) return null;
        if ((!pValid && LooksIntentional(p)) || (!cValid && LooksIntentional(c))) return null;

        (string Text, double Score)? best = null;
        void Consider(string text, double score)
        {
            if (best is null || score > best.Value.Score) best = (text, score);
        }

        if (c.Length >= 2) ConsiderSplit(p + c[0], c[1..], Consider);
        if (p.Length >= 2) ConsiderSplit(p[..^1], p[^1] + c, Consider);

        var merged = p + c;
        if (_lex.IsDictionaryWord(merged))
        {
            Consider(merged, _lex.LogFreq(merged) - CostWeight * SpaceCost);
        }
        else if (Options.Accents && _lex.TryGetByFold(TextUtil.Fold(merged), out var entries))
        {
            var e = PickVariant(TextUtil.Fold(merged), entries);
            Consider(e.Word, Score(e, SpaceCost + AccentCost));
        }

        if (best is null) return null;

        var independent = IndependentScore(p, pValid) + IndependentScore(c, cValid);
        if (best.Value.Score < independent + MinMargin) return null;

        var result = best.Value.Text;
        return prevCasing == Casing.Capitalized || (Options.Capitalize && sentenceStart)
            ? TextUtil.Capitalize(result)
            : result;
    }

    private void ConsiderSplit(string a, string b, Action<string, double> consider)
    {
        if (!IsSplitPart(a, -5.0) || !IsSplitPart(b, -5.0)) return;
        consider(a + " " + b, _lex.LogFreq(a) + _lex.LogFreq(b) - CostWeight * SpaceCost);
    }

    private bool IsSplitPart(string w, double minFreq)
    {
        if (w.Length == 1 && w is not ("a" or "y" or "o" or "e")) return false;
        return _lex.IsDictionaryWord(w) && _lex.LogFreq(w) >= minFreq;
    }

    private double IndependentScore(string lower, bool valid)
    {
        if (valid) return _lex.LogFreq(lower);
        return FindFix(lower)?.Score ?? -20.0;
    }

    /// <summary>Laughter, stretched words and abbreviations: typed on purpose, not typos.</summary>
    private static bool LooksIntentional(string lower)
    {
        if (lower.Length < 2) return true;

        var hasVowel = false;
        var run = 1;
        for (var i = 0; i < lower.Length; i++)
        {
            if (TextUtil.IsVowel(lower[i])) hasVowel = true;
            if (i > 0 && lower[i] == lower[i - 1]) { if (++run >= 3) return true; }
            else run = 1;
        }
        if (!hasVowel) return true;

        // jajaja, jejej, hahaha, jsjsjs
        if (lower.Length >= 3)
        {
            var distinct = new HashSet<char>(lower);
            if (distinct.Count == 2 && (distinct.Contains('j') || distinct.Contains('h'))) return true;
        }
        return false;
    }

    private (string Text, double Score)? FindFix(string lower)
    {
        var key = TextUtil.Fold(lower);
        var n = key.Length;
        var groups = new Dictionary<string, (string Text, double Score, bool Eligible)>();

        // A candidate too rare to be chosen still counts as a rival: it makes the choice ambiguous.
        void Offer(string candidateKey, double cost, double minFreq = MinCandidateFreq)
        {
            if (cost > MaxCost || !_lex.TryGetByFold(candidateKey, out var entries)) return;
            var e = PickVariant(candidateKey, entries);
            if (e.Word == lower) return;
            var score = Score(e, cost);
            if (!groups.TryGetValue(candidateKey, out var existing) || score > existing.Score)
                groups[candidateKey] = (e.Word, score, e.LogFreq >= minFreq);
        }

        // An unaccented spelling of two real words is not a typo; guessing either one would be a coin flip.
        if (HasRivalAccentTwins(key)) return null;
        if (Options.Accents) Offer(key, AccentCost);

        if (Options.Typos)
        {
            var veryShort = n <= 2;
            var shortWord = n <= 3;

            for (var i = 0; i < n - 1; i++)
            {
                if (key[i] == key[i + 1]) continue;
                var chars = key.ToCharArray();
                (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
                Offer(new string(chars), 0.5 + (i == 0 ? 0.2 : 0));
            }

            if (!veryShort)
            {
                for (var i = 0; i < n; i++)
                {
                    var first = i == 0 ? 0.4 : 0;

                    // The user hit an extra key.
                    if (!shortWord) Offer(key.Remove(i, 1), ExtraCost(key, i) + first);

                    // The user hit the wrong key.
                    foreach (var ch in Alphabet)
                    {
                        if (ch == key[i]) continue;
                        var isEnye = key[i] == 'n' && ch == 'ñ';
                        if (shortWord && !isEnye) continue;
                        var cost = isEnye ? 0.3 : SubstitutionCost(key[i], ch) + first;
                        Offer(string.Concat(key.AsSpan(0, i), new ReadOnlySpan<char>(in ch), key.AsSpan(i + 1)), cost);
                    }
                }

                // The user skipped a key.
                for (var i = 0; i <= n; i++)
                {
                    var first = i == 0 ? 0.4 : 0;
                    foreach (var ch in Alphabet)
                    {
                        var cost = MissingCost(key, i, ch) + first;
                        Offer(key.Insert(i, ch.ToString()), cost, shortWord ? ShortWordMinFreq : MinCandidateFreq);
                    }
                }

                // The user skipped the space bar.
                for (var i = 1; i < n; i++)
                {
                    var a = lower[..i];
                    var b = lower[i..];
                    if (!IsSplitPart(a, -4.6) || !IsSplitPart(b, -4.6)) continue;
                    var penalty = (_lex.IsSpanish(a) ? 0 : EnglishPenalty) + (_lex.IsSpanish(b) ? 0 : EnglishPenalty);
                    var score = _lex.LogFreq(a) + _lex.LogFreq(b) - CostWeight * SpaceCost - penalty;
                    var text = a + " " + b;
                    if (!groups.TryGetValue(text, out var existing) || score > existing.Score)
                        groups[text] = (text, score, true);
                }
            }
        }

        if (groups.Count == 0) return null;

        (string Text, double Score, bool Eligible) best = default;
        var bestScore = double.NegativeInfinity;
        var second = double.NegativeInfinity;
        foreach (var g in groups.Values)
        {
            if (g.Score > bestScore)
            {
                second = bestScore;
                bestScore = g.Score;
                best = g;
            }
            else if (g.Score > second)
            {
                second = g.Score;
            }
        }

        var margin = n <= 3 ? ShortWordMargin : MinMargin;
        if (!best.Eligible || bestScore < MinScore || bestScore - second < margin) return null;
        return (best.Text!, bestScore);
    }

    /// <summary>"envio" could be "envío" or "envió": two accented readings of similar weight, so do not guess.</summary>
    private bool HasRivalAccentTwins(string key)
    {
        if (!_lex.TryGetByFold(key, out var entries) || entries.Length < 2) return false;
        double best = double.NegativeInfinity, second = double.NegativeInfinity;
        foreach (var e in entries)
        {
            if (e.English) continue;
            if (e.LogFreq > best) (second, best) = (best, e.LogFreq);
            else if (e.LogFreq > second) second = e.LogFreq;
        }
        return best - second < 0.6;
    }

    /// <summary>
    /// Among words that differ only by accents, prefer the plain one when it exists
    /// ("como" over "cómo"): the user did not type an accent and both are valid.
    /// </summary>
    private static Lexicon.Entry PickVariant(string foldedKey, Lexicon.Entry[] entries)
    {
        var best = entries[0];
        var bestRank = double.NegativeInfinity;
        foreach (var e in entries)
        {
            if (e.Word == foldedKey && !e.English) return e;
            var rank = e.LogFreq - (e.English ? EnglishPenalty : 0);
            if (rank > bestRank)
            {
                bestRank = rank;
                best = e;
            }
        }
        return best;
    }

    private static double Score(Lexicon.Entry e, double cost) =>
        e.LogFreq - CostWeight * cost - (e.English ? EnglishPenalty : 0);

    private static double SubstitutionCost(char typed, char intended)
    {
        if (Confusable.Contains((typed, intended))) return 0.7;
        return AreAdjacent(typed, intended) ? 0.8 : 1.6;
    }

    /// <summary>Cost of the user having left out <paramref name="missing"/> at <paramref name="index"/>.</summary>
    private static double MissingCost(string key, int index, char missing)
    {
        var prev = index > 0 ? key[index - 1] : '\0';
        var next = index < key.Length ? key[index] : '\0';
        if (missing == prev || missing == next) return 0.6;
        return missing == 'h' ? 0.7 : 0.9;
    }

    /// <summary>Cost of the user having typed the extra character at <paramref name="index"/>.</summary>
    private static double ExtraCost(string key, int index)
    {
        var ch = key[index];
        var prev = index > 0 ? key[index - 1] : '\0';
        var next = index < key.Length - 1 ? key[index + 1] : '\0';
        if (ch == prev || ch == next) return 0.6;
        return AreAdjacent(ch, prev) || AreAdjacent(ch, next) ? 1.1 : 1.4;
    }

    private static bool AreAdjacent(char a, char b)
    {
        if (!KeyPos.TryGetValue(a, out var pa) || !KeyPos.TryGetValue(b, out var pb)) return false;
        var dx = Math.Abs(pa.X - pb.X);
        var dy = Math.Abs(pa.Row - pb.Row);
        return dy == 0 ? dx <= 1.01 : dy == 1 && dx <= 0.8;
    }

    private static Dictionary<char, (double, int)> BuildKeyPositions()
    {
        var map = new Dictionary<char, (double, int)>();
        string[] rows = ["qwertyuiop", "asdfghjklñ", "zxcvbnm"];
        double[] offsets = [0, 0.25, 0.75];
        for (var r = 0; r < rows.Length; r++)
            for (var i = 0; i < rows[r].Length; i++)
                map[rows[r][i]] = (i + offsets[r], r);
        return map;
    }

    private static HashSet<(char, char)> BuildConfusable()
    {
        var set = new HashSet<(char, char)>();
        foreach (var pair in new[] { "bv", "sc", "sz", "cz", "gj", "yi", "mn", "ck", "cq", "kq" })
        {
            set.Add((pair[0], pair[1]));
            set.Add((pair[1], pair[0]));
        }
        return set;
    }
}
