using LibreFlow.Core.Speech;

namespace LibreFlow.Tests;

public class FormattingTests
{
    [Fact]
    public void A_dictation_turned_into_a_numbered_list_still_counts_as_faithful() =>
        Assert.True(SpeechText.IsFaithfulCleanup(
            "bueno tengo que hacer tres cosas hoy comprar pan llamar a juan y mandar el presupuesto",
            "Tengo que hacer tres cosas hoy:\n\n1. Comprar pan.\n2. Llamar a Juan.\n3. Mandar el presupuesto."));

    [Fact]
    public void Line_breaks_become_windows_line_breaks_for_pasting() =>
        Assert.Equal("Uno:\r\n\r\n1. Dos.\r\n2. Tres.", SpeechText.WindowsLines("Uno:\n\n1. Dos.\r\n2. Tres."));

    [Fact]
    public void More_than_one_blank_line_is_reduced_to_one() =>
        Assert.Equal("Uno.\r\n\r\nDos.", SpeechText.WindowsLines("Uno.\n\n\n\nDos."));

    [Fact]
    public void Spaces_at_the_end_of_lines_and_extra_blank_lines_are_removed() =>
        Assert.Equal("Uno:\n\n1. Dos.\n2. Tres.", SpeechText.NormalizeLayout("Uno:  \r\n\n\n1. Dos.  \n2. Tres.\n"));

    [Fact]
    public void With_structure_on_the_model_is_asked_for_paragraphs_and_lists()
    {
        var prompt = Prompts.Cleanup(AppKind.General, toneByApp: true, vocabulary: "", structure: true);

        Assert.Contains("Paragraphs:", prompt);
        Assert.Contains("\"1. \"", prompt);
        Assert.Contains("title", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_chat_message_never_gets_titles()
    {
        var prompt = Prompts.Cleanup(AppKind.Chat, toneByApp: true, vocabulary: "", structure: true);

        Assert.Contains("Titles: none", prompt);
        Assert.DoesNotContain("##", prompt);
    }

    [Fact]
    public void Technical_tools_get_markdown_headings()
    {
        var prompt = Prompts.Cleanup(AppKind.Technical, toneByApp: true, vocabulary: "", structure: true);

        Assert.Contains("## ", prompt);
    }

    [Fact]
    public void With_structure_off_the_model_is_told_to_keep_one_block()
    {
        var prompt = Prompts.Cleanup(AppKind.General, toneByApp: true, vocabulary: "", structure: false);

        Assert.Contains("single block", prompt);
        Assert.DoesNotContain("Paragraphs:", prompt);
    }
}
