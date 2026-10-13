using System;

namespace StickyNotes;

public sealed class NoteData
{
    public const double DefaultSize = 280;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = DefaultSize;
    public double Height { get; set; } = DefaultSize;
    public string Color { get; set; } = AppSettings.Current.DefaultColor;
    public bool Pinned { get; set; }

    /// <summary>Keeps the note above all other windows.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>Rich content serialized as WPF FlowDocument XAML.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Plain-text fallback in case the rich content can't be loaded.</summary>
    public string PlainText { get; set; } = string.Empty;

    /// <summary>Whether the note is collapsed down to its title bar.</summary>
    public bool IsCollapsed { get; set; }

    /// <summary>Active or past reminders for this note.</summary>
    public System.Collections.Generic.List<NoteReminder> Reminders { get; set; } = new();
}
