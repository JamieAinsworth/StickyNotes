using System;
using Xunit;

namespace StickyNotes.Tests;

public class DeletedNoteTests
{
    private static DeletedNote Note(string plainText, DateTime? deletedAt = null) =>
        new(new NoteData { PlainText = plainText }, deletedAt ?? DateTime.Now);

    [Theory]
    [InlineData("Shopping\nmilk", "Shopping")]
    [InlineData("\n\n  Hello  \nworld", "Hello")]
    [InlineData("\u2611 buy milk", "buy milk")]
    [InlineData("\u2610 call Sam", "call Sam")]
    [InlineData("", "(empty note)")]
    [InlineData("  \n  ", "(empty note)")]
    [InlineData("\u2610", "(empty note)")]
    public void The_title_is_the_first_line_of_text(string plainText, string expected)
    {
        Assert.Equal(expected, Note(plainText).Title);
    }

    [Fact]
    public void Long_titles_are_shortened_with_an_ellipsis()
    {
        var title = Note(new string('x', 100)).Title;

        Assert.Equal(40, title.Length);
        Assert.EndsWith("\u2026", title);
    }

    [Fact]
    public void Missing_text_gives_the_empty_note_title()
    {
        Assert.Equal("(empty note)", new DeletedNote(new NoteData { PlainText = null! }, DateTime.Now).Title);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(5.5 * 60, "5 min ago")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(2.2 * 3600, "2 h ago")]
    public void The_age_is_described_in_plain_words(double secondsAgo, string expected)
    {
        Assert.Equal(expected, Note("x", DateTime.Now.AddSeconds(-secondsAgo)).AgeText);
    }
}
