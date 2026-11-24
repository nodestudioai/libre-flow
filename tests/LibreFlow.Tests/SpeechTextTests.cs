using LibreFlow.Core.Speech;

namespace LibreFlow.Tests;

public class SpeechTextTests
{
    [Fact]
    public void Replaces_a_misheard_word_with_the_dictionary_spelling() =>
        Assert.Equal("hablé con Vercel ayer",
            SpeechText.ApplyReplacements("hablé con bercel ayer", [new("bercel", "Vercel")]));

    [Fact]
    public void Replacement_ignores_case_and_only_matches_whole_words() =>
        Assert.Equal("Vercel y bercels",
            SpeechText.ApplyReplacements("Bercel y bercels", [new("bercel", "Vercel")]));

    [Fact]
    public void Expands_a_snippet_inside_a_sentence() =>
        Assert.Equal("escribime a nombre@ejemplo.com mañana",
            SpeechText.ApplyReplacements("escribime a mi mail mañana", [new("mi mail", "nombre@ejemplo.com")]));

    [Fact]
    public void Expands_a_snippet_said_on_its_own_dropping_the_final_period() =>
        Assert.Equal("nombre@ejemplo.com",
            SpeechText.ApplyReplacements("Mi mail.", [new("mi mail", "nombre@ejemplo.com")]));

    [Fact]
    public void Longer_triggers_win_over_shorter_ones() =>
        Assert.Equal("B",
            SpeechText.ApplyReplacements("mi mail de trabajo", [new("mi mail", "A"), new("mi mail de trabajo", "B")]));

    [Theory]
    [InlineData("Subtítulos realizados por la comunidad de Amara.org")]
    [InlineData("¡Gracias por ver el video!")]
    [InlineData("Thanks for watching!")]
    [InlineData("  ")]
    [InlineData(".")]
    public void Detects_what_the_speech_model_invents_on_silence(string text) =>
        Assert.True(SpeechText.IsHallucination(text));

    [Fact]
    public void Real_speech_is_not_a_hallucination() =>
        Assert.False(SpeechText.IsHallucination("Gracias por ver el documento que te mandé"));

    [Fact]
    public void Vocabulary_hint_lists_dictionary_words_within_the_limit()
    {
        var hint = SpeechText.VocabularyHint(Enumerable.Range(0, 500).Select(i => "Palabra" + i), maxChars: 200);
        Assert.StartsWith("Palabra0, Palabra1", hint);
        Assert.True(hint.Length <= 200);
    }

    [Theory]
    [InlineData("hola mundo", "Hola mundo")]
    [InlineData("  ya está  ", "Ya está")]
    public void Tidy_trims_and_capitalizes_raw_text(string raw, string expected) =>
        Assert.Equal(expected, SpeechText.Tidy(raw));

    [Fact]
    public void Rejects_a_cleanup_that_is_much_longer_than_what_was_said() =>
        Assert.False(SpeechText.IsFaithfulCleanup("cuál es la capital de francia",
            "La capital de Francia es París, una ciudad conocida por la Torre Eiffel y su rica historia cultural."));

    [Fact]
    public void Accepts_a_cleanup_that_keeps_the_content() =>
        Assert.True(SpeechText.IsFaithfulCleanup("eh bueno mandame el este el presupuesto mañana",
            "Mandame el presupuesto mañana."));
}
