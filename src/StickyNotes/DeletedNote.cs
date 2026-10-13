using System;
using System.Linq;

namespace StickyNotes;

/// <summary>A deleted note that can be restored, with a short label for menus.</summary>
public sealed record DeletedNote(NoteData Data, DateTime DeletedAt)
{
    private const int MaxTitleLength = 40;

    /// <summary>The first line of text, or a placeholder for an empty note.</summary>
    public string Title
    {
        get
        {
            var line = (Data.PlainText ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? string.Empty;

            // Checklist items start with a box character; the text after it is what identifies the note.
            if (line.Length > 0 && (line[0] == '\u2610' || line[0] == '\u2611')) line = line[1..].TrimStart();

            if (line.Length == 0) return "(empty note)";
            return line.Length > MaxTitleLength ? line[..(MaxTitleLength - 1)] + "…" : line;
        }
    }

    public string AgeText
    {
        get
        {
            var age = DateTime.Now - DeletedAt;
            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
            return $"{(int)age.TotalHours} h ago";
        }
    }
}
