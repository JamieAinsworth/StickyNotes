using System;
using Xunit;

namespace StickyNotes.Tests;

public class NoteReminderTests
{
    private static readonly DateTime Afternoon = new(2026, 10, 13, 14, 0, 0);

    [Fact]
    public void A_one_off_reminder_is_due_only_once_its_time_has_come()
    {
        var reminder = NoteReminder.Create(Afternoon.AddMinutes(30), recurring: false, now: Afternoon);

        Assert.False(reminder.IsDue(Afternoon));
        Assert.False(reminder.IsDue(Afternoon.AddMinutes(29)));
        Assert.True(reminder.IsDue(Afternoon.AddMinutes(30)));
        Assert.True(reminder.IsDue(Afternoon.AddDays(2)));
    }

    [Fact]
    public void A_one_off_reminder_fires_once()
    {
        var reminder = NoteReminder.Create(Afternoon, recurring: false, now: Afternoon.AddHours(-1));
        Assert.True(reminder.IsDue(Afternoon));

        reminder.MarkFired(Afternoon);

        Assert.True(reminder.HasFired);
        Assert.False(reminder.IsDue(Afternoon.AddSeconds(1)));
        Assert.False(reminder.IsDue(Afternoon.AddDays(1)));
    }

    [Fact]
    public void A_daily_reminder_for_a_time_later_today_fires_today_then_again_tomorrow()
    {
        var reminder = NoteReminder.Create(Afternoon.Date.AddHours(18), recurring: true, now: Afternoon);

        Assert.False(reminder.IsDue(Afternoon));
        Assert.True(reminder.IsDue(Afternoon.Date.AddHours(18)));

        reminder.MarkFired(Afternoon.Date.AddHours(18));
        Assert.False(reminder.IsDue(Afternoon.Date.AddHours(19)));

        Assert.False(reminder.IsDue(Afternoon.Date.AddDays(1).AddHours(17)));
        Assert.True(reminder.IsDue(Afternoon.Date.AddDays(1).AddHours(18)));
    }

    [Fact]
    public void A_daily_reminder_for_a_time_already_past_today_waits_until_tomorrow()
    {
        var reminder = NoteReminder.Create(Afternoon.Date.AddHours(9), recurring: true, now: Afternoon);

        Assert.False(reminder.IsDue(Afternoon));
        Assert.False(reminder.IsDue(Afternoon.AddHours(5)));
        Assert.False(reminder.IsDue(Afternoon.Date.AddDays(1).AddHours(8)));
        Assert.True(reminder.IsDue(Afternoon.Date.AddDays(1).AddHours(9)));
    }

    [Fact]
    public void A_daily_reminder_for_exactly_now_waits_until_tomorrow()
    {
        var reminder = NoteReminder.Create(Afternoon, recurring: true, now: Afternoon);

        Assert.False(reminder.IsDue(Afternoon));
        Assert.False(reminder.IsDue(Afternoon.AddSeconds(1)));
        Assert.True(reminder.IsDue(Afternoon.AddDays(1)));
    }

    [Fact]
    public void A_daily_reminder_loaded_from_an_earlier_day_is_due_once_its_time_passes()
    {
        var reminder = new NoteReminder
        {
            TargetTime = Afternoon.Date.AddHours(9),
            IsRecurring = true,
            LastFiredDate = Afternoon.Date.AddDays(-1),
        };

        Assert.True(reminder.IsDue(Afternoon));
        reminder.MarkFired(Afternoon);
        Assert.Equal(Afternoon.Date, reminder.LastFiredDate);
        Assert.False(reminder.IsDue(Afternoon.AddMinutes(1)));
    }

    [Fact]
    public void Reminders_on_one_note_are_independent()
    {
        var morning = NoteReminder.Create(Afternoon.Date.AddHours(9), recurring: true, now: Afternoon.Date.AddHours(8));
        var evening = NoteReminder.Create(Afternoon.Date.AddHours(18), recurring: true, now: Afternoon.Date.AddHours(8));

        Assert.True(morning.IsDue(Afternoon));
        morning.MarkFired(Afternoon);

        Assert.False(morning.IsDue(Afternoon));
        Assert.False(evening.IsDue(Afternoon));
        Assert.True(evening.IsDue(Afternoon.Date.AddHours(18)));
    }
}
