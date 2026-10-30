using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace StickyNotes.Tests;

/// <summary>Typing into checklist items through a real note window, the way the keyboard does.</summary>
[Collection(NotesCollection.Name)]
public class ChecklistTypingTests
{
    private const string Box = EditorHost.Empty;
    private const string Tick = EditorHost.Done;

    /// <summary>Types text at the caret. Each character is inserted separately, like key presses, then queued work runs.</summary>
    private static void Type(RichTextBox editor, string text)
    {
        foreach (var character in text)
        {
            var caret = editor.CaretPosition;
            editor.Selection.Select(caret, caret);
            editor.Selection.Text = character.ToString();
            editor.CaretPosition = editor.Selection.End;
            UiThread.Pump();
        }
    }

    private static Paragraph FirstItem(RichTextBox editor) =>
        editor.Document.Blocks.OfType<List>().First().ListItems.First().Blocks.OfType<Paragraph>().First();

    private static TextRange Content(Paragraph item)
    {
        var start = EditorHost.After(EditorHost.Start(item), 2);
        return new TextRange(start, item.ContentEnd);
    }

    private static bool Struck(TextRange range) => EditorHost.Has(range, TextDecorationLocation.Strikethrough);

    private static bool Grey(TextRange range) =>
        range.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush brush &&
        brush.Color == Color.FromRgb(0x77, 0x77, 0x77);

    /// <summary>A new checklist item with no text yet, caret after its box, in a real note.</summary>
    private static (RichTextBox Editor, Paragraph Item) EmptyChecklistItem(NoteWindow note)
    {
        var editor = ManagerScope.EditorOf(note);
        ManagerScope.SetText(note, "x");
        var paragraph = editor.Document.Blocks.OfType<Paragraph>().First();
        new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text = string.Empty;
        editor.Selection.Select(paragraph.ContentStart, paragraph.ContentStart);
        ChecklistEditor.Toggle(editor);

        var item = FirstItem(editor);
        Assert.Equal($"{Box} ", ChecklistEditor.ParagraphText(item));
        editor.CaretPosition = item.ContentEnd;
        Keyboard.Focus(editor);
        return (editor, item);
    }

    [Fact]
    public void Typing_into_an_item_ticked_before_it_had_any_text_is_struck_through_like_the_rest()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var (editor, item) = EmptyChecklistItem(note);

            ChecklistEditor.ToggleDone(editor, item);   // ticked while the item is still empty
            editor.CaretPosition = item.ContentEnd;
            Type(editor, "buy milk");

            Assert.Equal($"{Tick} buy milk", ChecklistEditor.ParagraphText(item));
            Assert.True(Struck(Content(item)));
            Assert.True(Grey(Content(item)));
        });
    }

    [Fact]
    public void Typing_into_an_empty_ticked_item_without_repositioning_the_caret_is_struck_through()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var (editor, item) = EmptyChecklistItem(note);

            // Click the box (the caret stays put), then carry on typing.
            ChecklistEditor.ToggleDone(editor, item);
            Type(editor, "ab");

            Assert.Equal($"{Tick} ab", ChecklistEditor.ParagraphText(item));
            Assert.True(Struck(Content(item)));
            Assert.True(Grey(Content(item)));
        });
    }

    [Fact]
    public void Typing_into_a_ticked_item_that_already_has_text_stays_struck_through()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var (editor, item) = EmptyChecklistItem(note);
            Type(editor, "milk");
            ChecklistEditor.ToggleDone(editor, item);

            editor.CaretPosition = item.ContentEnd;
            Type(editor, " and eggs");

            Assert.Equal($"{Tick} milk and eggs", ChecklistEditor.ParagraphText(item));
            Assert.True(ChecklistEditor.IsDone(item));
            Assert.True(Struck(Content(item)));
        });
    }

    [Fact]
    public void Typing_into_an_unticked_item_is_not_struck_through()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var (editor, item) = EmptyChecklistItem(note);

            Type(editor, "todo");

            Assert.Equal($"{Box} todo", ChecklistEditor.ParagraphText(item));
            Assert.False(Struck(Content(item)));
            Assert.False(Grey(Content(item)));
        });
    }

    [Fact]
    public void Unticking_an_empty_item_and_typing_gives_plain_text()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var (editor, item) = EmptyChecklistItem(note);
            ChecklistEditor.ToggleDone(editor, item);
            ChecklistEditor.ToggleDone(editor, item);   // ticked then unticked, still empty

            editor.CaretPosition = item.ContentEnd;
            Type(editor, "later");

            Assert.Equal($"{Box} later", ChecklistEditor.ParagraphText(item));
            Assert.False(Struck(Content(item)));
            Assert.False(Grey(Content(item)));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Typing_outside_ticked_content_leaves_text_unformatted(bool beforeCheckbox)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var editor = ManagerScope.EditorOf(note);
            Paragraph paragraph;
            if (beforeCheckbox)
            {
                (editor, paragraph) = EmptyChecklistItem(note);
                ChecklistEditor.ToggleDone(editor, paragraph);
                editor.CaretPosition = EditorHost.Start(paragraph);
            }
            else
            {
                ManagerScope.SetText(note, "plain");
                paragraph = editor.Document.Blocks.OfType<Paragraph>().First();
                editor.CaretPosition = paragraph.ContentEnd;
            }
            Keyboard.Focus(editor);

            Type(editor, beforeCheckbox ? "before " : " text");

            Assert.Equal(beforeCheckbox ? $"before {Tick} " : "plain text", ChecklistEditor.ParagraphText(paragraph));
            var typed = beforeCheckbox
                ? new TextRange(EditorHost.Start(paragraph), EditorHost.After(EditorHost.Start(paragraph), "before ".Length))
                : new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
            Assert.False(Struck(typed));
            Assert.False(Grey(typed));
        });
    }
}
