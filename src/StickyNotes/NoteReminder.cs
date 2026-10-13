using System;

namespace StickyNotes;

public sealed class NoteReminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Target date and time for the reminder, or the reference time-of-day for recurring reminders.</summary>
    public DateTime TargetTime { get; set; }

    /// <summary>Whether the reminder repeats every day at the specified time.</summary>
    public bool IsRecurring { get; set; }

    /// <summary>True if a non-recurring reminder has already triggered.</summary>
    public bool HasFired { get; set; }

    /// <summary>The date on which this reminder last fired, to prevent multiple alerts on the same day for recurring reminders.</summary>
    public DateTime? LastFiredDate { get; set; }
}
