using System.Text;

namespace LibreFlow.Core.Autocorrect;

/// <summary>Keys to send to the focused app: press Backspace N times, then type the text.</summary>
public sealed record TextEdit(int Backspaces, string Text);

public enum CorrectionKind { Typo, Capitalization, Spacing }

public sealed record CorrectionEvent(string Original, string Corrected, CorrectionKind Kind);

/// <summary>
/// Mirrors what the user has typed on the current line so a word can be judged when
/// they press space or Enter. It never sees the real text box, so anything that could
/// move the caret (click, arrows, shortcuts, window change) must call <see cref="Reset"/>.
/// Not thread-safe: call it only from the keyboard hook thread.
/// </summary>
public sealed class TypingTracker
{
    private const string LeadingPunctuation = "¿¡(\"'«[";
    private const string TrailingPunctuation = ".,;:!?)\"'»]…";
    private const int MaxLine = 600;

    private readonly CorrectionEngine _engine;
    private readonly StringBuilder _line = new();
    private bool _lineStartKnown;
    private Token? _previous;
    private Undo? _undo;

    private sealed record Token(int Start, int End, string Original, bool SentenceStart);
    private sealed record Undo(string Applied, string Original, string OriginalWord, string CorrectedWord, bool Learnable);

    public TypingTracker(CorrectionEngine engine) => _engine = engine;

    public bool Enabled { get; set; } = true;

    /// <summary>True while the focused field must not be touched (a password box).</summary>
    public bool Suppressed { get; set; }

    public event Action<CorrectionEvent>? Corrected;
    public event Action<CorrectionEvent>? Undone;

    /// <summary>True when nothing has been typed since the last reset.</summary>
    public bool IsEmpty => _line.Length == 0;

    public void Reset()
    {
        _line.Clear();
        _lineStartKnown = false;
        _previous = null;
        _undo = null;
    }

    public void OnChar(char c)
    {
        _undo = null;
        if (char.IsWhiteSpace(c) || char.IsControl(c))
        {
            Reset();
            return;
        }
        _line.Append(c);
        if (_line.Length > MaxLine)
        {
            _line.Remove(0, _line.Length - MaxLine / 2);
            _previous = null;
            _lineStartKnown = false;
        }
    }

    /// <summary>Space pressed. Returns the keys to send instead of the space, or null to let it through.</summary>
    public TextEdit? OnSpace()
    {
        _undo = null;
        var edit = Active ? Evaluate(" ") : null;
        if (edit is null) _line.Append(' ');
        return edit;
    }

    /// <summary>
    /// Enter pressed. Returns the keys to send before the Enter, or null. A lone word is
    /// never touched: it may be a password, a command or a search.
    /// </summary>
    public TextEdit? OnEnter()
    {
        _undo = null;
        var hasContext = HasSeveralWords();
        var edit = Active && hasContext && _engine.Options.CorrectOnEnter ? Evaluate("") : null;
        _undo = null;
        _line.Clear();
        _previous = null;
        _lineStartKnown = hasContext;
        return edit;
    }

    /// <summary>Backspace pressed. Right after a correction it undoes it; returns null otherwise.</summary>
    public TextEdit? OnBackspace()
    {
        if (_undo is { } undo)
        {
            _undo = null;
            _previous = null;
            _line.Length -= undo.Applied.Length;
            _line.Append(undo.Original);
            if (undo.Learnable) _engine.RegisterUndo(undo.OriginalWord);
            Undone?.Invoke(new CorrectionEvent(undo.OriginalWord, undo.CorrectedWord, CorrectionKind.Typo));
            return new TextEdit(undo.Applied.Length, undo.Original);
        }

        if (_line.Length == 0)
        {
            _lineStartKnown = false;
            return null;
        }
        _line.Length--;
        if (_previous is not null && _line.Length <= _previous.End) _previous = null;
        return null;
    }

    private bool Active => Enabled && !Suppressed;

    private bool HasSeveralWords()
    {
        var sawSpace = false;
        for (var i = 0; i < _line.Length; i++)
        {
            if (_line[i] == ' ') sawSpace = true;
            else if (sawSpace) return true;
        }
        return false;
    }

    private TextEdit? Evaluate(string boundary)
    {
        var tokenStart = _line.Length;
        while (tokenStart > 0 && _line[tokenStart - 1] != ' ') tokenStart--;
        if (tokenStart == _line.Length) return null;
        var token = _line.ToString(tokenStart, _line.Length - tokenStart);

        var lead = 0;
        while (lead < token.Length && LeadingPunctuation.Contains(token[lead])) lead++;
        var trail = token.Length;
        while (trail > lead && TrailingPunctuation.Contains(token[trail - 1])) trail--;
        var core = token[lead..trail];
        if (!TextUtil.IsAllLetters(core))
        {
            _previous = null;
            return null;
        }
        var leading = token[..lead];
        var trailing = token[trail..];
        var sentenceStart = IsSentenceStart(tokenStart, leading);

        if (_previous is { } prev && prev.End + 1 == tokenStart && lead == 0)
        {
            var pair = _engine.CorrectPair(prev.Original, core, prev.SentenceStart);
            if (pair is not null)
            {
                var deleted = _line.ToString(prev.Start, _line.Length - prev.Start);
                var applied = pair + trailing + boundary;
                _line.Length = prev.Start;
                _line.Append(applied);
                _previous = null;
                _undo = boundary.Length > 0
                    ? new Undo(applied, prev.Original + " " + token + boundary, core, pair, Learnable: false)
                    : null;
                Corrected?.Invoke(new CorrectionEvent(prev.Original + " " + core, pair, CorrectionKind.Spacing));
                return new TextEdit(deleted.Length, applied);
            }
        }

        var coreStart = tokenStart + lead;
        var fixedWord = _engine.CorrectWord(core, sentenceStart);
        if (fixedWord is null)
        {
            _previous = trailing.Length == 0
                ? new Token(coreStart, coreStart + core.Length, core, sentenceStart)
                : null;
            return null;
        }

        var replacement = leading + fixedWord + trailing + boundary;
        _line.Length = tokenStart;
        _line.Append(replacement);
        _previous = trailing.Length == 0
            ? new Token(coreStart, coreStart + fixedWord.Length, core, sentenceStart)
            : null;

        var caseOnly = string.Equals(core, fixedWord, StringComparison.OrdinalIgnoreCase);
        var kind = caseOnly ? CorrectionKind.Capitalization
            : fixedWord.Contains(' ') ? CorrectionKind.Spacing
            : CorrectionKind.Typo;
        _undo = boundary.Length > 0
            ? new Undo(replacement, token + boundary, core, fixedWord, Learnable: !caseOnly)
            : null;
        Corrected?.Invoke(new CorrectionEvent(core, fixedWord, kind));
        return new TextEdit(token.Length, replacement);
    }

    private bool IsSentenceStart(int tokenStart, string leading)
    {
        if (leading.Contains('¿') || leading.Contains('¡')) return true;
        var i = tokenStart - 1;
        while (i >= 0 && _line[i] == ' ') i--;
        if (i < 0) return _lineStartKnown;
        while (i >= 0 && (_line[i] is ')' or '"' or '\'' or '»')) i--;
        return i >= 0 && _line[i] is '.' or '!' or '?' or '…';
    }
}
