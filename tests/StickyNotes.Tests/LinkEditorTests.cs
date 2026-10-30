using System;
using System.Linq;
using System.Windows.Documents;
using Xunit;

namespace StickyNotes.Tests;

[Collection(NotesCollection.Name)]
public class LinkEditorTests
{
    [Theory]
    [InlineData("see https://example.com now", "https://example.com/")]
    [InlineData("www.example.com/a?b=1.", "https://www.example.com/a?b=1")]
    [InlineData("(https://en.wikipedia.org/wiki/Foo_(bar))", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    public void Finds_web_addresses_without_trailing_punctuation(string text, string expected)
    {
        var link = Assert.Single(LinkEditor.Find(text));
        Assert.Equal(expected, link.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("me@www.example.com")]
    [InlineData("plain text")]
    public void Ignores_anything_that_is_not_a_web_address(string text) => Assert.Empty(LinkEditor.Find(text));

    [Fact]
    public void Only_web_addresses_can_be_opened() => Assert.Null(LinkEditor.ToUri("file:///C:/x.exe"));

    [Fact]
    public void Relink_wraps_addresses_and_keeps_the_text()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("go to https://example.com today", "nothing here");

            Assert.True(LinkEditor.Relink(host.Editor, includeCaret: true));

            var paragraph = host.Editor.Document.Blocks.OfType<Paragraph>().First();
            var link = Assert.Single(paragraph.Inlines.OfType<Hyperlink>());
            Assert.Equal("https://example.com", new TextRange(link.ContentStart, link.ContentEnd).Text);
            Assert.Equal("go to https://example.com today", new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text);
            Assert.False(LinkEditor.Relink(host.Editor, includeCaret: true));
        });
    }

    [Fact]
    public void An_address_still_being_typed_is_not_linked_until_the_caret_moves_on()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("https://example.com");
            var paragraph = host.Editor.Document.Blocks.OfType<Paragraph>().First();
            host.Editor.CaretPosition = paragraph.ContentEnd;

            Assert.False(LinkEditor.Relink(host.Editor, includeCaret: false));
            Assert.True(LinkEditor.Relink(host.Editor, includeCaret: true));
        });
    }

    [Fact]
    public void Breaking_an_address_removes_its_link()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("https://example.com");
            LinkEditor.Relink(host.Editor, includeCaret: true);
            var paragraph = host.Editor.Document.Blocks.OfType<Paragraph>().First();

            new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text = "no link";
            LinkEditor.Relink(host.Editor, includeCaret: true);

            Assert.Empty(paragraph.Inlines.OfType<Hyperlink>());
        });
    }

    [Fact]
    public void Links_survive_saving_and_reloading_a_note()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("read https://example.com later");
            note.RelinkNow(includeCaret: true);
            var data = note.CaptureData();

            scope.Manager.DeleteNote(note);
            var restored = scope.Manager.UndoDelete(data)!;
            UiThread.Pump();

            var editor = ManagerScope.EditorOf(restored);
            var link = editor.Document.Blocks.OfType<Paragraph>().First().Inlines.OfType<Hyperlink>().Single();
            Assert.Equal(new Uri("https://example.com"), LinkEditor.UriOf(link));
        });
    }

    [Fact]
    public void Undoing_a_link_is_not_immediately_redone()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var editor = ManagerScope.EditorOf(note);
            ManagerScope.SetText(note, "see https://example.com now");
            UiThread.Pump();
            var paragraph = editor.Document.Blocks.OfType<Paragraph>().First();
            Assert.Single(paragraph.Inlines.OfType<Hyperlink>());

            editor.Undo();
            UiThread.Pump();

            Assert.Empty(editor.Document.Blocks.OfType<Paragraph>().First().Inlines.OfType<Hyperlink>());
            Assert.True(editor.CanRedo);
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [Fact]
    public void Deleting_the_focused_note_returns_focus_to_another_app_instead_of_a_note()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var other = scope.CreateNote("other");
            var deleted = scope.CreateNote("deleted");
            UiThread.Pump();
            deleted.ActivateNote();
            UiThread.Pump();
            var previousApp = NativeMethods.TopOtherAppWindow();
            if (GetForegroundWindow() != new System.Windows.Interop.WindowInteropHelper(deleted).Handle || previousApp == IntPtr.Zero)
                return; // Windows didn't give the test the foreground, or there's no other app to hand it to.

            scope.Manager.DeleteNote(deleted);
            UiThread.Pump();

            Assert.Equal(previousApp, GetForegroundWindow());
            Assert.False(other.IsActive);
            Assert.Null(scope.Manager.ActivatingNote);
        });
    }
}
