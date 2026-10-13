using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;

namespace StickyNotes;

/// <summary>Text formatting helpers shared by the editor's shortcuts and checklists.</summary>
internal static class TextFormatting
{
    /// <summary>
    /// Adds or removes strikethrough run by run. Decorations can differ from word to word (one word underlined,
    /// say), so each run keeps its own other decorations instead of being overwritten with one shared set.
    /// </summary>
    public static void SetStrikethrough(TextRange range, bool strike)
    {
        foreach (var segment in TextSegments(range))
        {
            var current = segment.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            segment.ApplyPropertyValue(Inline.TextDecorationsProperty, WithStrikethrough(current, strike));
        }
    }

    /// <summary>True when every piece of text in the range is struck through.</summary>
    public static bool IsFullyStruck(TextRange range)
    {
        var segments = TextSegments(range);
        return segments.Count > 0 && segments.All(s =>
            s.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection d &&
            d.Any(x => x.Location == TextDecorationLocation.Strikethrough));
    }

    public static TextDecorationCollection WithStrikethrough(TextDecorationCollection? current, bool strike)
    {
        var updated = new TextDecorationCollection();
        if (current != null)
        {
            foreach (var decoration in current.Where(d => d.Location != TextDecorationLocation.Strikethrough))
                updated.Add(decoration.Clone());
        }
        if (strike)
        {
            foreach (var decoration in TextDecorations.Strikethrough) updated.Add(decoration.Clone());
        }
        return updated;
    }

    /// <summary>The range split into pieces that each lie within a single run of text (so share one formatting).</summary>
    private static List<TextRange> TextSegments(TextRange range)
    {
        var segments = new List<TextRange>();
        var pointer = range.Start;
        while (pointer != null && pointer.CompareTo(range.End) < 0)
        {
            var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
            if (next == null) break;
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var end = next.CompareTo(range.End) > 0 ? range.End : next;
                segments.Add(new TextRange(pointer, end));
            }
            pointer = next;
        }
        return segments;
    }
}
