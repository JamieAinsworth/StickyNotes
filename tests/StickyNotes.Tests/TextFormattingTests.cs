using System.Windows;
using System.Windows.Documents;
using Xunit;

namespace StickyNotes.Tests;

public class TextFormattingTests
{
    private static Paragraph Mixed()
    {
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run("plain "));
        paragraph.Inlines.Add(new Run("under") { TextDecorations = TextDecorations.Underline });
        paragraph.Inlines.Add(new Run(" end"));
        return paragraph;
    }

    [Fact]
    public void Strikethrough_is_applied_to_every_run_and_other_decorations_are_kept()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            var paragraph = Mixed();
            host.SetBlocks(paragraph);
            var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);

            Assert.False(TextFormatting.IsFullyStruck(range));
            TextFormatting.SetStrikethrough(range, strike: true);

            Assert.True(TextFormatting.IsFullyStruck(range));
            var underlined = host.TextOf(paragraph, 6, 5);
            Assert.True(EditorHost.Has(underlined, TextDecorationLocation.Underline));
            Assert.True(EditorHost.Has(underlined, TextDecorationLocation.Strikethrough));

            TextFormatting.SetStrikethrough(range, strike: false);

            Assert.False(TextFormatting.IsFullyStruck(range));
            Assert.False(EditorHost.Has(underlined, TextDecorationLocation.Strikethrough));
            Assert.True(EditorHost.Has(underlined, TextDecorationLocation.Underline));
        });
    }

    [Fact]
    public void A_partly_struck_range_is_not_fully_struck_so_toggling_strikes_all_of_it()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            var paragraph = Mixed();
            host.SetBlocks(paragraph);
            TextFormatting.SetStrikethrough(host.TextOf(paragraph, 0, 5), strike: true);
            var whole = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);

            Assert.False(TextFormatting.IsFullyStruck(whole));
            TextFormatting.SetStrikethrough(whole, !TextFormatting.IsFullyStruck(whole));

            Assert.True(TextFormatting.IsFullyStruck(whole));
        });
    }

    [Fact]
    public void WithStrikethrough_only_touches_strikethrough()
    {
        var underline = new TextDecorationCollection(TextDecorations.Underline);

        var added = TextFormatting.WithStrikethrough(underline, strike: true);
        Assert.Equal(2, added.Count);

        var removed = TextFormatting.WithStrikethrough(added, strike: false);
        Assert.Single(removed);
        Assert.Equal(TextDecorationLocation.Underline, removed[0].Location);

        Assert.Empty(TextFormatting.WithStrikethrough(null, strike: false));
    }
}
