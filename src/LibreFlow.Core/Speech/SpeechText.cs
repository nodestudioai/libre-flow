using System.Text;
using System.Text.RegularExpressions;
using LibreFlow.Core.Autocorrect;

namespace LibreFlow.Core.Speech;

public sealed record Replacement(string From, string To);

/// <summary>Pure text steps of the dictation pipeline.</summary>
public static class SpeechText
{
    private static readonly string[] Hallucinations =
    [
        "subtitulos realizados por la comunidad de amara org",
        "subtitulos por la comunidad de amara org",
        "gracias por ver el video",
        "gracias por ver",
        "muchas gracias por ver el video",
        "suscribete",
        "suscribete al canal",
        "no olvides suscribirte",
        "thanks for watching",
        "thank you for watching",
        "thank you",
        "you",
        "bye",
        "musica",
        "aplausos",
    ];

    /// <summary>Dictionary replacements and voice snippets, applied in one pass so results are never re-replaced.</summary>
    public static string ApplyReplacements(string text, IEnumerable<Replacement> replacements)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in replacements)
        {
            var from = r.From.Trim();
            if (from.Length > 0) map[from] = r.To;
        }
        if (map.Count == 0) return text;

        // The whole dictation was a trigger ("Mi mail."): return just the expansion.
        var bare = text.Trim().TrimEnd('.', '!', '?', '…').Trim();
        if (map.TryGetValue(bare, out var whole)) return whole;

        var alternation = string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        var pattern = $@"(?<![\p{{L}}\p{{N}}])(?:{alternation})(?![\p{{L}}\p{{N}}])";
        return Regex.Replace(text, pattern, m => map[m.Value], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Speech models invent stock phrases when given silence or noise.</summary>
    public static bool IsHallucination(string text)
    {
        var norm = Normalize(text);
        if (norm.Length == 0) return true;
        if (norm.Contains("amara org")) return true;
        return Array.IndexOf(Hallucinations, norm) >= 0;
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(TextUtil.FoldChar(ch));
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Comma-separated vocabulary used to bias the speech model towards the user's words.</summary>
    public static string VocabularyHint(IEnumerable<string> words, int maxChars = 600)
    {
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            var word = w.Trim();
            if (word.Length == 0) continue;
            var extra = (sb.Length == 0 ? 0 : 2) + word.Length;
            if (sb.Length + extra > maxChars) break;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(word);
        }
        return sb.ToString();
    }

    public static string Tidy(string raw)
    {
        var text = Regex.Replace(raw.Trim(), @"\s+", " ");
        return TextUtil.Capitalize(text);
    }

    /// <summary>
    /// Guards against the cleanup model answering the dictation instead of cleaning it:
    /// the result may be shorter, but not much longer, and must reuse the spoken words.
    /// </summary>
    public static bool IsFaithfulCleanup(string raw, string cleaned)
    {
        var rawWords = Normalize(raw).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cleanWords = Normalize(cleaned).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (cleanWords.Length == 0) return rawWords.Length <= 2;
        if (cleanWords.Length > rawWords.Length * 1.3 + 4) return false;

        var spoken = new HashSet<string>(rawWords);
        var shared = cleanWords.Count(spoken.Contains);
        return shared >= cleanWords.Length * 0.5;
    }
}
