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

    /// <summary>
    /// Creates a reminder. A daily reminder for a time that has already passed today is treated as fired for
    /// today, so its first alert is tomorrow instead of the moment it is added.
    /// </summary>
    public static NoteReminder Create(DateTime targetTime, bool recurring, DateTime now)
    {
        var reminder = new NoteReminder { TargetTime = targetTime, IsRecurring = recurring };
        if (recurring && now.TimeOfDay >= targetTime.TimeOfDay) reminder.LastFiredDate = now.Date;
        return reminder;
    }

    /// <summary>Whether the reminder should alert at <paramref name="now"/>.</summary>
    public bool IsDue(DateTime now)
    {
        if (IsRecurring)
        {
            bool firedToday = LastFiredDate.HasValue && LastFiredDate.Value.Date == now.Date;
            return !firedToday && now.TimeOfDay >= TargetTime.TimeOfDay;
        }
        return !HasFired && now >= TargetTime;
    }

    /// <summary>Records that the reminder has alerted, so it doesn't alert again.</summary>
    public void MarkFired(DateTime now)
    {
        if (IsRecurring) LastFiredDate = now.Date;
        else HasFired = true;
    }
}
