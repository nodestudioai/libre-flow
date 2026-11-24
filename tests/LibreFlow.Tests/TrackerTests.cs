using System.Text;
using LibreFlow.Core.Autocorrect;

namespace LibreFlow.Tests;

[Collection("lexicon")]
public class TrackerTests
{
    private readonly CorrectionEngine _engine;
    private readonly TypingTracker _tracker;
    private readonly StringBuilder _screen = new();

    public TrackerTests(LexiconFixture fx)
    {
        _engine = fx.NewEngine();
        _tracker = new TypingTracker(_engine);
    }

    /// <summary>Feeds keys the way the keyboard hook does and mirrors what the target app would show.
    /// '\n' = Enter, '\b' = Backspace.</summary>
    private string Type(string keys)
    {
        foreach (var k in keys)
        {
            switch (k)
            {
                case ' ':
                    if (!Apply(_tracker.OnSpace())) _screen.Append(' ');
                    break;
                case '\n':
                    Apply(_tracker.OnEnter());
                    _screen.Append('\n');
                    break;
                case '\b':
                    if (!Apply(_tracker.OnBackspace()) && _screen.Length > 0) _screen.Length--;
                    break;
                default:
                    _tracker.OnChar(k);
                    _screen.Append(k);
                    break;
            }
        }
        return _screen.ToString();
    }

    private bool Apply(TextEdit? edit)
    {
        if (edit is null) return false;
        _screen.Length -= edit.Backspaces;
        _screen.Append(edit.Text);
        return true;
    }

    [Fact]
    public void Fixes_a_typo_when_space_is_pressed() => Assert.Equal("hola ", Type("ohla "));

    [Fact]
    public void Leaves_correct_text_untouched() => Assert.Equal("hola como estas ", Type("hola como estas "));

    [Fact]
    public void Fixes_the_users_example_with_a_misplaced_space() =>
        Assert.Equal("hola como estas ", Type("hol acomo estas "));

    [Fact]
    public void Keeps_trailing_punctuation_when_fixing() => Assert.Equal("hola, ", Type("ohla, "));

    [Fact]
    public void Honors_manual_backspace_edits_before_the_space() => Assert.Equal("hola ", Type("holx\ba "));

    [Fact]
    public void Backspace_right_after_a_correction_restores_what_was_typed() =>
        Assert.Equal("ohla ", Type("ohla \b"));

    [Fact]
    public void Typing_continues_normally_after_an_undo() => Assert.Equal("ohla que ", Type("ohla \bque "));

    [Fact]
    public void A_word_undone_once_is_not_corrected_again() =>
        Assert.Equal("ohla y ohla ", Type("ohla \by ohla "));

    [Fact]
    public void Reports_a_word_as_learned_after_two_undos()
    {
        var learned = new List<string>();
        _engine.WordLearned += learned.Add;
        Type("grcias \b");
        Assert.Empty(learned);
        _engine.ForgetSessionIgnores();
        Type("grcias \b");
        Assert.Equal(["grcias"], learned);
    }

    [Fact]
    public void Capitalizes_after_a_period() => Assert.Equal("Hola. Como estas ", Type("Hola. como estas "));

    [Fact]
    public void Does_not_capitalize_the_first_word_when_the_context_is_unknown() =>
        Assert.Equal("hola ", Type("hola "));

    [Fact]
    public void Capitalizes_the_line_after_a_multi_word_enter() =>
        Assert.Equal("hola como estas\nBien ", Type("hola como estas\nbien "));

    [Fact]
    public void Fixes_the_last_word_on_enter_when_the_line_has_several_words() =>
        Assert.Equal("hola como estas\n", Type("hola como estsa\n"));

    [Fact]
    public void Never_touches_a_single_word_followed_by_enter() =>
        Assert.Equal("estsa\n", Type("estsa\n"));

    [Theory]
    [InlineData("clave123 ")]
    [InlineData("usuario@gmial.com ")]
    [InlineData("archvo.txt ")]
    [InlineData("holq.123! ")]
    public void Never_touches_tokens_with_digits_or_symbols(string keys) => Assert.Equal(keys, Type(keys));

    [Fact]
    public void Forgets_the_word_in_progress_on_reset()
    {
        Type("oh");
        _tracker.Reset();
        Assert.Equal("ohla ", Type("la "));
    }

    [Fact]
    public void Does_nothing_while_disabled()
    {
        _tracker.Enabled = false;
        Assert.Equal("ohla ", Type("ohla "));
    }

    [Fact]
    public void Does_nothing_while_suppressed_for_a_password_field()
    {
        _tracker.Suppressed = true;
        Assert.Equal("ohla ", Type("ohla "));
    }

    [Fact]
    public void Raises_an_event_describing_each_correction()
    {
        var seen = new List<(string, string)>();
        _tracker.Corrected += c => seen.Add((c.Original, c.Corrected));
        Type("ohla ");
        Assert.Equal([("ohla", "hola")], seen);
    }
}
