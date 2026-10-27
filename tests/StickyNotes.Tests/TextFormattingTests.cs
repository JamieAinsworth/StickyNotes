using System.Linq;
using System.Windows;
using System.Windows.Documents;
using Xunit;

namespace StickyNotes.Tests;

public class TextFormattingTests
{
    [Theory]
    [InlineData(TextMarkerStyle.Disc)]
    [InlineData(TextMarkerStyle.Decimal)]
    [InlineData(TextMarkerStyle.None)]
    public void List_items_can_be_nested_and_outdented_without_losing_their_text_or_marker_style(TextMarkerStyle marker)
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            string prefix = marker == TextMarkerStyle.None ? EditorHost.Empty + " " : string.Empty;
            var parent = new Paragraph(new Run(prefix + "Parent"));
            var child = new Paragraph(new Run(prefix + "Child"));
            var list = new List { MarkerStyle = marker };
            list.ListItems.Add(new ListItem(parent));
            list.ListItems.Add(new ListItem(child));
            host.SetBlocks(list);
            host.Caret(child, 0);

            Assert.True(TextFormatting.ChangeListIndentation(host.Editor, outdent: false));
            var nested = Assert.IsType<List>(Assert.IsType<ListItem>(child.Parent).Parent);
            Assert.Same(parent.Parent, nested.Parent);
            Assert.Equal(marker, nested.MarkerStyle);
            Assert.Equal(prefix + "Child", ChecklistEditor.ParagraphText(child));

            Assert.True(TextFormatting.ChangeListIndentation(host.Editor, outdent: true));
            Assert.Same(list, Assert.IsType<ListItem>(child.Parent).Parent);
            Assert.Equal(2, list.ListItems.Count);
            Assert.Equal(prefix + "Child", ChecklistEditor.ParagraphText(child));
        });
    }

    [Fact]
    public void Selected_bullets_can_be_nested_as_a_group_and_undone_and_redone()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            var parent = new Paragraph(new Run("Parent"));
            var first = new Paragraph(new Run("First child"));
            var second = new Paragraph(new Run("Second child"));
            var list = new List(new ListItem(parent));
            list.ListItems.Add(new ListItem(first));
            list.ListItems.Add(new ListItem(second));
            host.SetBlocks(list);
            string original = host.Dump();
            host.Editor.Selection.Select(first.ContentStart, second.ContentEnd);

            Assert.True(TextFormatting.ChangeListIndentation(host.Editor, outdent: false));
            var nested = Assert.IsType<List>(Assert.IsType<ListItem>(first.Parent).Parent);
            Assert.Same(parent.Parent, nested.Parent);
            Assert.Equal(2, nested.ListItems.Count);
            Assert.Same(nested, Assert.IsType<ListItem>(second.Parent).Parent);
            string indented = host.Dump();

            host.Editor.Undo();
            Assert.Equal(original, host.Dump());
            host.Editor.Redo();
            Assert.Equal(indented, host.Dump());
        });
    }

    [Fact]
    public void Bullets_support_more_than_one_nested_level()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            var parent = new Paragraph(new Run("Parent"));
            var child = new Paragraph(new Run("Child"));
            var grandchild = new Paragraph(new Run("Grandchild"));
            var list = new List(new ListItem(parent));
            list.ListItems.Add(new ListItem(child));
            list.ListItems.Add(new ListItem(grandchild));
            host.SetBlocks(list);
            host.Editor.Selection.Select(child.ContentStart, grandchild.ContentEnd);
            Assert.True(TextFormatting.ChangeListIndentation(host.Editor, outdent: false));

            host.Caret(grandchild, 0);
            Assert.True(TextFormatting.ChangeListIndentation(host.Editor, outdent: false));
            var deepest = Assert.IsType<List>(Assert.IsType<ListItem>(grandchild.Parent).Parent);
            Assert.Same(child.Parent, deepest.Parent);

            Assert.True(TextFormatting.ChangeListIndentation(host.Editor, outdent: true));
            Assert.Same(Assert.IsType<ListItem>(child.Parent).Parent, Assert.IsType<ListItem>(grandchild.Parent).Parent);
        });
    }

    [Fact]
    public void List_indentation_leaves_plain_paragraphs_unchanged()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("Plain text");
            var paragraph = host.Editor.Document.Blocks.OfType<Paragraph>().Single();
            host.Caret(paragraph, 0);
            string original = host.Dump();

            Assert.False(TextFormatting.ChangeListIndentation(host.Editor, outdent: false));
            Assert.False(TextFormatting.ChangeListIndentation(host.Editor, outdent: true));
            Assert.Equal(original, host.Dump());
        });
    }

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
