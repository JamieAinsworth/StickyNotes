using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Xunit;

namespace StickyNotes.Tests;

public class ChecklistEditorTests
{
    private const string Box = EditorHost.Empty;
    private const string Tick = EditorHost.Done;

    [Fact]
    public void Plain_lines_become_checklist_items()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("alpha", "beta", "gamma");
            host.SelectParagraphs(0, 1);

            ChecklistEditor.Toggle(host.Editor);

            Assert.Equal($"LIST(None){{ P[{Box} alpha] P[{Box} beta] }} P[gamma]", host.Dump());
        });
    }

    [Fact]
    public void Toggling_again_removes_the_boxes_and_the_list()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("alpha", "beta");
            host.SelectParagraphs(0, 1);
            ChecklistEditor.Toggle(host.Editor);

            var items = host.ListItems();
            host.Editor.Selection.Select(items[0].ContentStart, items[1].ContentEnd);
            ChecklistEditor.Toggle(host.Editor);

            Assert.Equal("P[alpha] P[beta]", host.Dump());
        });
    }

    [Fact]
    public void Bullet_items_keep_their_bullet_and_gain_a_box()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("one", "two");
            host.SelectParagraphs(0, 1);
            EditingCommands.ToggleBullets.Execute(null, host.Editor);
            UiThread.Pump();

            var items = host.ListItems();
            host.Editor.Selection.Select(items[0].ContentStart, items[1].ContentEnd);
            ChecklistEditor.Toggle(host.Editor);
            Assert.Equal($"LIST(Disc){{ P[{Box} one] P[{Box} two] }}", host.Dump());

            ChecklistEditor.Toggle(host.Editor);
            Assert.Equal("LIST(Disc){ P[one] P[two] }", host.Dump());
        });
    }

    [Fact]
    public void Toggle_is_a_single_undo_step()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("alpha", "beta");
            host.SelectParagraphs(0, 1);
            host.Editor.IsUndoEnabled = false;
            host.Editor.IsUndoEnabled = true;

            ChecklistEditor.Toggle(host.Editor);
            host.Editor.Undo();

            Assert.Equal("P[alpha] P[beta]", host.Dump());
        });
    }

    [Fact]
    public void Enter_continues_the_checklist_with_an_unticked_box()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("plain", "x");
            host.SelectParagraphs(0, 0);
            ChecklistEditor.Toggle(host.Editor);
            host.Editor.CaretPosition = host.FirstItem().ContentEnd;
            Assert.True(ChecklistEditor.CaretInChecklistItem(host.Editor));

            host.PressEnter();
            ChecklistEditor.PrepareNewItem(host.Editor);

            Assert.Equal($"LIST(None){{ P[{Box} plain] P[{Box} ] }} P[x]", host.Dump());
        });
    }

    [Fact]
    public void Splitting_an_item_in_the_middle_prefixes_the_second_half()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("hello world");
            host.SelectParagraphs(0, 0);
            ChecklistEditor.Toggle(host.Editor);
            host.Caret(host.FirstItem(), 2 + 5);

            host.PressEnter();
            ChecklistEditor.PrepareNewItem(host.Editor);

            Assert.Equal($"LIST(None){{ P[{Box} hello] P[{Box}  world] }}", host.Dump());
        });
    }

    [Fact]
    public void Enter_on_an_empty_checklist_item_leaves_the_list_in_one_keypress()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("task");
            host.SelectParagraphs(0, 0);
            ChecklistEditor.Toggle(host.Editor);
            host.Editor.CaretPosition = host.FirstItem().ContentEnd;
            host.PressEnter();
            ChecklistEditor.PrepareNewItem(host.Editor);
            var empty = host.Editor.CaretPosition.Paragraph!;

            bool handled = ChecklistEditor.TryExitEmptyItem(host.Editor);

            Assert.True(handled);
            Assert.IsNotType<ListItem>(empty.Parent);
        });
    }

    [Fact]
    public void Enter_on_an_empty_box_in_a_bullet_item_defers_to_the_list_exit()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("keep", "bullet");
            host.SelectParagraphs(1, 1);
            EditingCommands.ToggleBullets.Execute(null, host.Editor);
            UiThread.Pump();
            host.Editor.CaretPosition = host.FirstItem().ContentEnd;
            var empty = host.PressEnter();
            host.Editor.Selection.Select(empty.ContentStart, empty.ContentStart);
            ChecklistEditor.Toggle(host.Editor);
            Assert.True(ChecklistEditor.HasCheckbox(empty));
            host.Caret(empty, 2);

            bool handled = ChecklistEditor.TryExitEmptyItem(host.Editor);

            // The box is gone but it is still a bullet item, so the caller's normal "empty list item" handling ends the list.
            Assert.False(handled);
            Assert.Equal(string.Empty, ChecklistEditor.ParagraphText(empty));
            Assert.IsType<ListItem>(empty.Parent);
        });
    }

    [Fact]
    public void Backspace_straight_after_the_box_removes_it_and_splits_the_list()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("a", "b", "c");
            host.SelectParagraphs(0, 2);
            ChecklistEditor.Toggle(host.Editor);
            var middle = host.ListItems()[1];
            host.Caret(middle, 2);

            Assert.True(ChecklistEditor.TryBackspaceAtCheckbox(host.Editor));

            Assert.Equal($"LIST(None){{ P[{Box} a] }} P[b] LIST(None){{ P[{Box} c] }}", host.Dump());
        });
    }

    [Fact]
    public void Ticking_swaps_the_box_and_strikes_through_the_text()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("buy milk");
            host.SelectParagraphs(0, 0);
            ChecklistEditor.Toggle(host.Editor);
            var item = host.FirstItem();

            ChecklistEditor.ToggleDone(host.Editor, item);

            Assert.True(ChecklistEditor.IsDone(item));
            Assert.Equal($"LIST(None){{ P[{Tick} buy milk] }}", host.Dump());
            Assert.True(EditorHost.Has(host.TextOf(item, 2, 8), TextDecorationLocation.Strikethrough));

            ChecklistEditor.ToggleDone(host.Editor, item);

            Assert.False(ChecklistEditor.IsDone(item));
            Assert.False(EditorHost.Has(host.TextOf(item, 2, 8), TextDecorationLocation.Strikethrough));
        });
    }

    [Fact]
    public void Ticking_keeps_other_decorations_such_as_underline()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Run("one "));
            paragraph.Inlines.Add(new Run("two") { TextDecorations = TextDecorations.Underline });
            paragraph.Inlines.Add(new Run(" three"));
            host.SetBlocks(paragraph);
            host.Select(paragraph);
            ChecklistEditor.Toggle(host.Editor);
            var item = host.FirstItem();
            var text = ChecklistEditor.ParagraphText(item);
            var two = host.TextOf(item, text.IndexOf("two"), 3);
            var one = host.TextOf(item, text.IndexOf("one"), 3);

            ChecklistEditor.ToggleDone(host.Editor, item);
            Assert.True(EditorHost.Has(two, TextDecorationLocation.Strikethrough));
            Assert.True(EditorHost.Has(two, TextDecorationLocation.Underline));
            Assert.True(EditorHost.Has(one, TextDecorationLocation.Strikethrough));
            Assert.False(EditorHost.Has(one, TextDecorationLocation.Underline));

            ChecklistEditor.ToggleDone(host.Editor, item);
            Assert.False(EditorHost.Has(two, TextDecorationLocation.Strikethrough));
            Assert.True(EditorHost.Has(two, TextDecorationLocation.Underline));
        });
    }

    [Fact]
    public void Ticking_is_a_single_undo_step()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("gamma");
            host.SelectParagraphs(0, 0);
            ChecklistEditor.Toggle(host.Editor);
            host.Editor.IsUndoEnabled = false;
            host.Editor.IsUndoEnabled = true;

            ChecklistEditor.ToggleDone(host.Editor, host.FirstItem());
            host.Editor.Undo();

            Assert.Equal($"LIST(None){{ P[{Box} gamma] }}", host.Dump());
            Assert.False(host.Editor.CanUndo);
        });
    }

    [Fact]
    public void Ticked_and_unticked_boxes_use_the_same_font_and_size()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("one", "two");
            host.SelectParagraphs(0, 1);
            ChecklistEditor.Toggle(host.Editor);
            var items = host.ListItems();
            ChecklistEditor.ToggleDone(host.Editor, items[0]);
            host.Editor.UpdateLayout();

            string FontOf(Paragraph p) =>
                host.TextOf(p, 0, 1).GetPropertyValue(TextElement.FontFamilyProperty)?.ToString() ?? string.Empty;
            double Width(Paragraph p) =>
                EditorHost.After(EditorHost.Start(p), 1).GetCharacterRect(LogicalDirection.Forward).Left -
                EditorHost.Start(p).GetCharacterRect(LogicalDirection.Forward).Left;
            double Height(Paragraph p) => EditorHost.Start(p).GetCharacterRect(LogicalDirection.Forward).Height;

            Assert.Contains("Segoe UI Symbol", FontOf(items[0]));
            Assert.Contains("Segoe UI Symbol", FontOf(items[1]));
            Assert.InRange(Width(items[0]) - Width(items[1]), -0.5, 0.5);
            Assert.InRange(Height(items[0]) - Height(items[1]), -0.5, 0.5);
        });
    }

    [Fact]
    public void Ticked_items_still_support_backspace_and_removal()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("a", "b");
            host.SelectParagraphs(0, 1);
            ChecklistEditor.Toggle(host.Editor);
            var items = host.ListItems();
            ChecklistEditor.ToggleDone(host.Editor, items[0]);
            ChecklistEditor.ToggleDone(host.Editor, items[1]);

            host.Caret(items[0], 2);
            Assert.True(ChecklistEditor.TryBackspaceAtCheckbox(host.Editor));
            Assert.Equal($"P[a] LIST(None){{ P[{Tick} b] }}", host.Dump());

            var remaining = host.ListItems()[0];
            host.Editor.Selection.Select(remaining.ContentStart, remaining.ContentEnd);
            ChecklistEditor.Toggle(host.Editor);
            Assert.Equal("P[a] P[b]", host.Dump());
            Assert.False(EditorHost.Has(host.TextOf(host.Editor.Document.Blocks.OfType<Paragraph>().Last(), 0, 1),
                TextDecorationLocation.Strikethrough));
        });
    }

    [Fact]
    public void A_new_item_split_from_a_ticked_one_is_plainly_formatted()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("buy milk");
            host.SelectParagraphs(0, 0);
            ChecklistEditor.Toggle(host.Editor);
            var ticked = host.FirstItem();
            ChecklistEditor.ToggleDone(host.Editor, ticked);
            host.Caret(ticked, 2 + 3);

            host.PressEnter();
            ChecklistEditor.PrepareNewItem(host.Editor);
            var split = host.Editor.CaretPosition.Paragraph!;
            var whole = new TextRange(split.ContentStart, split.ContentEnd);

            Assert.Equal($"{Box}  milk", ChecklistEditor.ParagraphText(split));
            Assert.False(EditorHost.Has(whole, TextDecorationLocation.Strikethrough));
            Assert.Equal(EditorHost.TextColour, ((SolidColorBrush)whole.GetPropertyValue(TextElement.ForegroundProperty)).Color);

            host.Editor.Selection.Text = "typed";
            var typed = new TextRange(split.ContentStart, split.ContentEnd);
            Assert.False(EditorHost.Has(typed, TextDecorationLocation.Strikethrough));
            Assert.Equal(EditorHost.TextColour, ((SolidColorBrush)typed.GetPropertyValue(TextElement.ForegroundProperty)).Color);

            Assert.True(ChecklistEditor.IsDone(ticked));
            Assert.True(EditorHost.Has(host.TextOf(ticked, 2, 3), TextDecorationLocation.Strikethrough));
        });
    }

    [Fact]
    public void Old_notes_get_the_symbol_font_pinned_on_their_boxes()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs($"{Tick} old ticked", $"{Box} old empty", "plain");
            var paragraphs = host.Editor.Document.Blocks.OfType<Paragraph>().ToList();

            string FontOf(Paragraph p) =>
                host.TextOf(p, 0, 1).GetPropertyValue(TextElement.FontFamilyProperty)?.ToString() ?? string.Empty;
            Assert.DoesNotContain("Segoe UI Symbol", FontOf(paragraphs[0]));

            ChecklistEditor.RefreshGlyphs(host.Editor.Document);

            Assert.Contains("Segoe UI Symbol", FontOf(paragraphs[0]));
            Assert.Contains("Segoe UI Symbol", FontOf(paragraphs[1]));
            Assert.DoesNotContain("Segoe UI Symbol", FontOf(paragraphs[2]));
            Assert.Equal($"P[{Tick} old ticked] P[{Box} old empty] P[plain]", host.Dump());
        });
    }

    [Fact]
    public void Checklists_survive_saving_and_loading()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("keep", "me");
            host.SelectParagraphs(0, 1);
            ChecklistEditor.Toggle(host.Editor);
            ChecklistEditor.ToggleDone(host.Editor, host.FirstItem());

            string xaml;
            using (var stream = new MemoryStream())
            {
                new TextRange(host.Editor.Document.ContentStart, host.Editor.Document.ContentEnd).Save(stream, DataFormats.Xaml);
                xaml = Encoding.UTF8.GetString(stream.ToArray());
            }
            host.Editor.Document.Blocks.Clear();
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(xaml)))
                new TextRange(host.Editor.Document.ContentStart, host.Editor.Document.ContentEnd).Load(stream, DataFormats.Xaml);
            UiThread.Pump();

            var items = host.ListItems();
            Assert.Equal($"{Tick} keep", ChecklistEditor.ParagraphText(items[0]));
            Assert.Equal($"{Box} me", ChecklistEditor.ParagraphText(items[1]));
            Assert.Contains("Segoe UI Symbol",
                host.TextOf(items[0], 0, 1).GetPropertyValue(TextElement.FontFamilyProperty)?.ToString() ?? string.Empty);

            // The saved note can be ticked and unticked again after loading.
            ChecklistEditor.ToggleDone(host.Editor, items[1]);
            ChecklistEditor.ToggleDone(host.Editor, items[0]);
            Assert.Equal($"LIST(None){{ P[{Box} keep] P[{Tick} me] }}", host.Dump());
        });
    }

    [Fact]
    public void Clicking_the_box_hits_it_but_clicking_the_text_does_not()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("task");
            host.SelectParagraphs(0, 0);
            ChecklistEditor.Toggle(host.Editor);
            host.Editor.UpdateLayout();
            var item = host.FirstItem();
            var box = EditorHost.Start(item).GetCharacterRect(LogicalDirection.Forward);

            var onBox = ChecklistEditor.ParagraphAtCheckbox(host.Editor, new Point(box.Left + 3, box.Top + box.Height / 2));
            var onText = ChecklistEditor.ParagraphAtCheckbox(host.Editor, new Point(box.Left + 60, box.Top + box.Height / 2));

            Assert.Same(item, onBox);
            Assert.Null(onText);
        });
    }
}
