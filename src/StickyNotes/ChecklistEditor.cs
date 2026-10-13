using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace StickyNotes;

/// <summary>
/// Checkbox support for the note editor. A checkbox is a leading "☐ " / "☑ " in the paragraph's text, so it
/// survives copy/paste and the plain-text fallback. A line with a checkbox and no bullet or number lives in a
/// list with no marker, which keeps Enter and indentation behaving like a list.
/// </summary>
internal static class ChecklistEditor
{
    private const char EmptyBox = '\u2610';
    private const char DoneBox = '\u2611';
    private const string Prefix = "\u2610 ";

    private static readonly Brush DoneBrush = CreateDoneBrush();

    private static Brush CreateDoneBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
        brush.Freeze();
        return brush;
    }

    /// <summary>The paragraph's own text. (TextRange.Text would also include the list marker of list items.)</summary>
    public static string ParagraphText(Paragraph paragraph)
    {
        var builder = new System.Text.StringBuilder();
        AppendText(paragraph.Inlines, builder);
        return builder.ToString();
    }

    private static void AppendText(InlineCollection inlines, System.Text.StringBuilder builder)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run: builder.Append(run.Text); break;
                case Span span: AppendText(span.Inlines, builder); break;
                case LineBreak: builder.Append('\n'); break;
            }
        }
    }

    private static string TextOf(Paragraph paragraph) => ParagraphText(paragraph);

    private static TextPointer PrefixStart(Paragraph paragraph) =>
        paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);

    public static bool HasCheckbox(Paragraph paragraph)
    {
        var text = TextOf(paragraph);
        return text.Length > 0 && (text[0] == EmptyBox || text[0] == DoneBox) && (text.Length == 1 || text[1] == ' ');
    }

    public static bool IsDone(Paragraph paragraph)
    {
        var text = TextOf(paragraph);
        return text.Length > 0 && text[0] == DoneBox;
    }

    public static bool IsChecklistList(List list) => list.MarkerStyle == TextMarkerStyle.None;

    /// <summary>
    /// Adds a checkbox to the selected lines, or removes it when they all have one. Plain lines become checklist
    /// items (checkbox instead of a bullet); bullet and numbered items keep their marker and gain a checkbox.
    /// </summary>
    public static void Toggle(RichTextBox editor)
    {
        var paragraphs = SelectedParagraphs(editor);
        if (paragraphs.Count == 0) return;

        bool hadSelection = !editor.Selection.IsEmpty;
        var caretParagraph = editor.CaretPosition.Paragraph;
        int caretOffset = caretParagraph == null ? 0 : PrefixStart(caretParagraph).GetOffsetToPosition(editor.CaretPosition);
        bool removing = paragraphs.All(HasCheckbox);
        int shift = 0;

        editor.BeginChange();
        try
        {
            if (removing)
            {
                shift = -(caretParagraph != null && TextOf(caretParagraph).Length >= 2 ? 2 : 1);
                foreach (var paragraph in paragraphs) RemoveCheckbox(paragraph, editor.Foreground);
            }
            else
            {
                shift = caretParagraph != null && !HasCheckbox(caretParagraph) ? Prefix.Length : 0;
                foreach (var paragraph in paragraphs.Where(p => !HasCheckbox(p))) AddCheckbox(paragraph);
                foreach (var run in PlainRuns(paragraphs)) WrapInChecklist(run);
            }

            if (hadSelection)
            {
                editor.Selection.Select(paragraphs[0].ContentStart, paragraphs[^1].ContentEnd);
            }
            else if (caretParagraph != null)
            {
                var position = PrefixStart(caretParagraph).GetPositionAtOffset(Math.Max(0, caretOffset + shift), LogicalDirection.Forward);
                editor.CaretPosition = position ?? caretParagraph.ContentEnd;
            }
        }
        finally
        {
            editor.EndChange();
        }
    }

    /// <summary>Flips the checkbox of a paragraph and strikes through / greys its text when done.</summary>
    public static void ToggleDone(RichTextBox editor, Paragraph paragraph)
    {
        if (!HasCheckbox(paragraph)) return;
        bool done = !IsDone(paragraph);

        var start = PrefixStart(paragraph);
        var glyphEnd = start.GetPositionAtOffset(1, LogicalDirection.Forward);
        if (glyphEnd == null) return;
        new TextRange(start, glyphEnd).Text = done ? DoneBox.ToString() : EmptyBox.ToString();


        StyleContent(paragraph, done, editor.Foreground);
    }

    private static void StyleContent(Paragraph paragraph, bool done, Brush normalBrush)
    {
        var contentStart = PrefixStart(paragraph).GetPositionAtOffset(2, LogicalDirection.Forward);
        if (contentStart == null || contentStart.CompareTo(paragraph.ContentEnd) >= 0) return;

        var content = new TextRange(contentStart, paragraph.ContentEnd);
        var current = content.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;

        var updated = new TextDecorationCollection();
        if (current != null)
        {
            foreach (var decoration in current.Where(d => d.Location != TextDecorationLocation.Strikethrough))
                updated.Add(decoration.Clone());
        }
        if (done)
        {
            foreach (var decoration in TextDecorations.Strikethrough) updated.Add(decoration.Clone());
        }

        content.ApplyPropertyValue(Inline.TextDecorationsProperty, updated);
        content.ApplyPropertyValue(TextElement.ForegroundProperty, done ? DoneBrush : normalBrush);
    }

    private static void AddCheckbox(Paragraph paragraph)
    {
        var start = PrefixStart(paragraph);
        new TextRange(start, start).Text = Prefix;
    }

    private static void RemoveCheckbox(Paragraph paragraph, Brush normalBrush)
    {
        if (!HasCheckbox(paragraph)) return;
        if (IsDone(paragraph)) StyleContent(paragraph, done: false, normalBrush);

        var start = PrefixStart(paragraph);
        var length = TextOf(paragraph).Length >= 2 ? 2 : 1;
        var end = start.GetPositionAtOffset(length, LogicalDirection.Forward) ?? paragraph.ContentEnd;
        new TextRange(start, end).Text = string.Empty;

        if (paragraph.Parent is ListItem { List: { } list } && IsChecklistList(list)) ExitList(paragraph);
    }

    /// <summary>Wraps plain paragraphs in a marker-less list so Enter and Tab treat them as list items.</summary>
    private static void WrapInChecklist(List<Paragraph> run)
    {
        var first = run[0];
        var blocks = first.SiblingBlocks;
        if (blocks == null) return;

        var list = new List { MarkerStyle = TextMarkerStyle.None, Padding = new Thickness(0) };
        blocks.InsertBefore(first, list);
        foreach (var paragraph in run)
        {
            blocks.Remove(paragraph);
            list.ListItems.Add(new ListItem(paragraph));
        }
    }

    /// <summary>Takes a paragraph out of its list, splitting the list around it.</summary>
    public static void ExitList(Paragraph paragraph)
    {
        if (paragraph.Parent is not ListItem { List: { } list } item || item.Blocks.Count != 1) return;
        var blocks = list.SiblingBlocks;
        if (blocks == null) return;

        var following = list.ListItems.SkipWhile(i => i != item).Skip(1).ToList();

        item.Blocks.Remove(paragraph);
        list.ListItems.Remove(item);
        foreach (var other in following) list.ListItems.Remove(other);

        blocks.InsertAfter(list, paragraph);
        if (following.Count > 0)
        {
            var tail = new List { MarkerStyle = list.MarkerStyle, Padding = list.Padding };
            foreach (var other in following) tail.ListItems.Add(other);
            blocks.InsertAfter(paragraph, tail);
        }
        if (list.ListItems.Count == 0) blocks.Remove(list);
    }

    /// <summary>Consecutive selected paragraphs that aren't list items yet.</summary>
    private static IEnumerable<List<Paragraph>> PlainRuns(List<Paragraph> paragraphs)
    {
        List<Paragraph>? run = null;
        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Parent is ListItem)
            {
                if (run != null) yield return run;
                run = null;
            }
            else if (run != null && run[^1].NextBlock == paragraph)
            {
                run.Add(paragraph);
            }
            else
            {
                if (run != null) yield return run;
                run = new List<Paragraph> { paragraph };
            }
        }
        if (run != null) yield return run;
    }

    private static List<Paragraph> SelectedParagraphs(RichTextBox editor)
    {
        var selection = editor.Selection;
        var result = new List<Paragraph>();

        var first = selection.Start.GetInsertionPosition(LogicalDirection.Forward).Paragraph;
        if (first != null) result.Add(first);

        var pointer = selection.Start.GetNextInsertionPosition(LogicalDirection.Forward);
        while (pointer != null && pointer.CompareTo(selection.End) < 0)
        {
            var paragraph = pointer.Paragraph;
            if (paragraph != null && !result.Contains(paragraph)) result.Add(paragraph);
            pointer = pointer.GetNextInsertionPosition(LogicalDirection.Forward);
        }
        return result;
    }

    public static bool CaretInChecklistItem(RichTextBox editor) =>
        editor.Selection.IsEmpty && editor.CaretPosition.Paragraph is { } p && HasCheckbox(p);

    public static bool SelectionHasCheckbox(RichTextBox editor) =>
        editor.Selection.Start.GetInsertionPosition(LogicalDirection.Forward).Paragraph is { } p && HasCheckbox(p);

    /// <summary>
    /// Enter on an item that holds only a checkbox removes the checkbox (and leaves a checklist), like Enter
    /// on an empty bullet leaves a list.
    /// </summary>
    public static bool TryExitEmptyItem(RichTextBox editor)
    {
        if (!editor.Selection.IsEmpty) return false;
        var paragraph = editor.CaretPosition.Paragraph;
        if (paragraph == null || !HasCheckbox(paragraph)) return false;
        if (TextOf(paragraph).Trim(' ').Length != 1) return false;

        editor.BeginChange();
        try
        {
            RemoveCheckbox(paragraph, editor.Foreground);
            editor.CaretPosition = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        }
        finally
        {
            editor.EndChange();
        }
        return true;
    }

    /// <summary>Backspace straight after the checkbox removes the checkbox instead of just its space.</summary>
    public static bool TryBackspaceAtCheckbox(RichTextBox editor)
    {
        if (!editor.Selection.IsEmpty) return false;
        var paragraph = editor.CaretPosition.Paragraph;
        if (paragraph == null || !HasCheckbox(paragraph)) return false;

        int offset = PrefixStart(paragraph).GetOffsetToPosition(editor.CaretPosition);
        int prefixLength = TextOf(paragraph).Length >= 2 ? 2 : 1;
        if (offset != prefixLength) return false;

        editor.BeginChange();
        try
        {
            RemoveCheckbox(paragraph, editor.Foreground);
            editor.CaretPosition = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        }
        finally
        {
            editor.EndChange();
        }
        return true;
    }

    /// <summary>After Enter has split a checkbox item, gives the new item its own empty checkbox.</summary>
    public static void PrepareNewItem(RichTextBox editor)
    {
        var paragraph = editor.CaretPosition.Paragraph;
        if (paragraph == null || HasCheckbox(paragraph)) return;

        editor.BeginChange();
        try
        {
            AddCheckbox(paragraph);
            editor.CaretPosition = PrefixStart(paragraph).GetPositionAtOffset(2, LogicalDirection.Forward) ?? paragraph.ContentEnd;
        }
        finally
        {
            editor.EndChange();
        }
    }

    /// <summary>The paragraph whose checkbox is under the given point (relative to the editor), if any.</summary>
    public static Paragraph? ParagraphAtCheckbox(RichTextBox editor, Point point)
    {
        var paragraph = editor.GetPositionFromPoint(point, snapToText: false)?.Paragraph;
        if (paragraph == null || !HasCheckbox(paragraph)) return null;

        var start = PrefixStart(paragraph);
        var end = start.GetPositionAtOffset(1, LogicalDirection.Forward);
        if (end == null) return null;

        var first = start.GetCharacterRect(LogicalDirection.Forward);
        var second = end.GetCharacterRect(LogicalDirection.Forward);
        if (first.IsEmpty || second.IsEmpty) return null;

        bool over = point.X >= first.Left - 2 && point.X <= second.Left + 2 &&
                    point.Y >= first.Top && point.Y <= first.Bottom;
        return over ? paragraph : null;
    }
}
