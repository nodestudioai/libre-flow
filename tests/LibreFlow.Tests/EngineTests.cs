using LibreFlow.Core.Autocorrect;

namespace LibreFlow.Tests;

public sealed class LexiconFixture
{
    public Lexicon Lexicon { get; } = Lexicon.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
    public CorrectionEngine NewEngine() => new(Lexicon);
}

[CollectionDefinition("lexicon")]
public sealed class LexiconCollection : ICollectionFixture<LexiconFixture> { }

[Collection("lexicon")]
public class EngineTests(LexiconFixture fx)
{
    private readonly CorrectionEngine _engine = fx.NewEngine();

    [Theory]
    [InlineData("ohla", "hola")]
    [InlineData("qeu", "que")]
    [InlineData("neceisto", "necesito")]
    [InlineData("problmea", "problema")]
    [InlineData("peude", "puede")]
    [InlineData("grcias", "gracias")]
    [InlineData("tengp", "tengo")]
    [InlineData("hol", "hola")]
    [InlineData("estsa", "estas")]
    [InlineData("holaa", "hola")]
    [InlineData("desarollo", "desarrollo")]
    public void Fixes_common_typos(string typed, string expected) =>
        Assert.Equal(expected, _engine.CorrectWord(typed, sentenceStart: false));

    [Theory]
    [InlineData("tambien", "también")]
    [InlineData("asi", "así")]
    [InlineData("informacion", "información")]
    [InlineData("dia", "día")]
    [InlineData("despues", "después")]
    [InlineData("manana", "mañana")]
    [InlineData("tambein", "también")]
    [InlineData("dificl", "difícil")]
    public void Adds_unambiguous_accents(string typed, string expected) =>
        Assert.Equal(expected, _engine.CorrectWord(typed, sentenceStart: false));

    [Theory]
    [InlineData("tenes", "tenés")]
    [InlineData("queres", "querés")]
    [InlineData("venis", "venís")]
    public void Restores_the_accent_on_voseo_forms(string typed, string expected) =>
        Assert.Equal(expected, _engine.CorrectWord(typed, sentenceStart: false));

    [Theory]
    [InlineData("sabes")]
    [InlineData("hablas")]
    [InlineData("hola")]
    [InlineData("como")]
    [InlineData("estas")]
    [InlineData("esta")]
    [InlineData("que")]
    [InlineData("mas")]
    [InlineData("si")]
    [InlineData("tu")]
    [InlineData("el")]
    [InlineData("solo")]
    public void Leaves_valid_words_alone_even_when_an_accented_twin_exists(string typed) =>
        Assert.Null(_engine.CorrectWord(typed, sentenceStart: false));

    [Theory]
    [InlineData("tenés")]
    [InlineData("querés")]
    [InlineData("podés")]
    [InlineData("sabés")]
    [InlineData("decime")]
    [InlineData("haceme")]
    [InlineData("averigualo")]
    [InlineData("fijate")]
    [InlineData("mirá")]
    [InlineData("vení")]
    [InlineData("sos")]
    public void Leaves_rioplatense_voseo_alone(string typed) =>
        Assert.Null(_engine.CorrectWord(typed, sentenceStart: false));

    [Theory]
    [InlineData("meeting")]
    [InlineData("the")]
    [InlineData("hello")]
    [InlineData("jajaja")]
    [InlineData("jajajaj")]
    [InlineData("holaaaa")]
    [InlineData("xq")]
    [InlineData("tmb")]
    [InlineData("npm")]
    [InlineData("git")]
    [InlineData("bun")]
    [InlineData("WhatsApp")]
    [InlineData("iPhone")]
    [InlineData("NASA")]
    [InlineData("abc123")]
    [InlineData("Brandix")]
    [InlineData("cas")]
    [InlineData("ets")]
    [InlineData("envio")]
    public void Leaves_english_slang_code_and_ambiguous_words_alone(string typed) =>
        Assert.Null(_engine.CorrectWord(typed, sentenceStart: false));

    [Theory]
    [InlineData("holacomo", "hola como")]
    [InlineData("porfavor", "por favor")]
    public void Splits_words_typed_without_a_space(string typed, string expected) =>
        Assert.Equal(expected, _engine.CorrectWord(typed, sentenceStart: false));

    [Fact]
    public void Capitalizes_a_valid_word_at_sentence_start() =>
        Assert.Equal("Hola", _engine.CorrectWord("hola", sentenceStart: true));

    [Fact]
    public void Capitalizes_and_fixes_at_sentence_start() =>
        Assert.Equal("Hola", _engine.CorrectWord("ohla", sentenceStart: true));

    [Fact]
    public void Does_not_capitalize_when_the_option_is_off()
    {
        _engine.Options.Capitalize = false;
        Assert.Null(_engine.CorrectWord("hola", sentenceStart: true));
    }

    [Fact]
    public void Does_not_add_accents_when_the_option_is_off()
    {
        _engine.Options.Accents = false;
        Assert.Null(_engine.CorrectWord("tambien", sentenceStart: false));
    }

    [Fact]
    public void Never_corrects_a_word_from_the_personal_dictionary()
    {
        Assert.Equal("gracias", _engine.CorrectWord("grcias", false));
        _engine.SetUserWords(["Grcias"]);
        Assert.Null(_engine.CorrectWord("grcias", false));
    }

    [Theory]
    [InlineData("hol", "acomo", "hola como")]
    [InlineData("holac", "omo", "hola como")]
    [InlineData("hol", "a", "hola")]
    [InlineData("tam", "bien", "también")]
    public void Repairs_a_misplaced_space_between_two_words(string prev, string current, string expected) =>
        Assert.Equal(expected, _engine.CorrectPair(prev, current, sentenceStart: false));

    [Theory]
    [InlineData("hola", "como")]
    [InlineData("de", "lso")]
    [InlineData("es", "otros")]
    public void Leaves_pairs_alone_when_no_space_shift_explains_them(string prev, string current) =>
        Assert.Null(_engine.CorrectPair(prev, current, sentenceStart: false));
}
