using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace StickyNotes.Tests;

[Collection(NotesCollection.Name)]
public class NoteWindowTests
{
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    private static object? Call(NoteWindow note, string method, params object?[] args) =>
        typeof(NoteWindow).GetMethod(method, NonPublic)!.Invoke(note, args);

    private static T Field<T>(NoteWindow note, string name) =>
        (T)typeof(NoteWindow).GetField(name, NonPublic)!.GetValue(note)!;

    private static void CollapseAndHover(NoteWindow note)
    {
        Call(note, "ApplyCollapsed", true, true);
        UiThread.Wait(50);
        Call(note, "Header_MouseEnter", null, null);
    }

    [Fact]
    public void Ctrl_Shift_Z_restores_the_most_recently_deleted_note()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var survivor = scope.CreateNote("survivor");
            scope.Manager.DeleteNote(scope.CreateNote("older"));
            scope.Manager.DeleteNote(scope.CreateNote("newest"));
            UiThread.Pump();
            var both = ModifierKeys.Control | ModifierKeys.Shift;

            Assert.True(survivor.TryHandleUndoDeleteKey(Key.Z, both));
            UiThread.Pump();

            Assert.Contains(scope.OpenNotes(), n => ManagerScope.TextOf(n) == "newest");
            Assert.Single(scope.Manager.RecentlyDeleted());
            Assert.Equal("older", scope.Manager.RecentlyDeleted()[0].Title);
        });
    }

    [Theory]
    [InlineData(Key.Z, ModifierKeys.Control)]
    [InlineData(Key.Z, ModifierKeys.Shift)]
    [InlineData(Key.Z, ModifierKeys.None)]
    [InlineData(Key.Y, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(Key.Z, ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)]
    public void Other_key_combinations_do_not_restore_a_note(Key key, ModifierKeys modifiers)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var survivor = scope.CreateNote("survivor");
            scope.Manager.DeleteNote(scope.CreateNote("deleted"));
            UiThread.Pump();

            Assert.False(survivor.TryHandleUndoDeleteKey(key, modifiers));

            Assert.Single(scope.OpenNotes());
            Assert.True(scope.Manager.CanUndoDelete);
        });
    }

    [Fact]
    public void Ctrl_Shift_Z_is_left_alone_when_nothing_has_been_deleted()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("only note");

            Assert.False(note.TryHandleUndoDeleteKey(Key.Z, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.Single(scope.OpenNotes());
        });
    }

    [Fact]
    public void Hovering_a_collapsed_note_restores_the_height_it_had_before_collapsing()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            note.Height = 340;
            UiThread.Pump();
            note.UpdateLayout();

            Call(note, "ApplyCollapsed", true, true);
            UiThread.Pump();
            Assert.InRange(note.Height, 31, 33);

            Call(note, "Header_MouseEnter", null, null);
            UiThread.Pump();
            Assert.InRange(note.Height, 339, 341);
        });
    }

    [Fact]
    public void Deleting_a_note_while_hover_expanded_stops_its_timer_and_raises_no_errors()
    {
        UiThread.Run(() =>
        {
            while (UiThread.Unhandled.TryDequeue(out _)) { }
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            CollapseAndHover(note);
            var timer = Field<DispatcherTimer>(note, "_hoverTimer");
            Assert.True(timer.IsEnabled);

            scope.Manager.DeleteNote(note);
            UiThread.Wait(500);

            Assert.False(timer.IsEnabled);
            Assert.Empty(UiThread.Unhandled);
        });
    }

    [Fact]
    public void Reloading_with_a_hover_expanded_note_stops_its_timer()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text");
            CollapseAndHover(note);
            var timer = Field<DispatcherTimer>(note, "_hoverTimer");

            scope.Manager.Reload();

            Assert.False(timer.IsEnabled);
        });
    }

    [Fact]
    public void The_bell_goes_back_to_an_outline_once_a_one_off_reminder_has_fired()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var filled = (FrameworkElement)note.FindName("ReminderBellFilled");
            var outline = (FrameworkElement)note.FindName("ReminderBellOutline");

            Assert.Equal(Visibility.Collapsed, filled.Visibility);

            var reminder = NoteReminder.Create(DateTime.Now.AddHours(1), recurring: false, now: DateTime.Now);
            note.Data.Reminders.Add(reminder);
            Call(note, "UpdateReminderButton");
            Assert.Equal(Visibility.Visible, filled.Visibility);
            Assert.Equal(Visibility.Collapsed, outline.Visibility);

            reminder.MarkFired(DateTime.Now);
            note.TriggerReminderAlert();
            UiThread.Pump();

            Assert.Equal(Visibility.Collapsed, filled.Visibility);
            Assert.Equal(Visibility.Visible, outline.Visibility);
            note.DismissReminderAlert();
        });
    }

    [Fact]
    public void The_bell_stays_filled_while_a_daily_reminder_remains()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var filled = (FrameworkElement)note.FindName("ReminderBellFilled");
            var oneOff = NoteReminder.Create(DateTime.Now.AddHours(1), recurring: false, now: DateTime.Now);
            note.Data.Reminders.Add(oneOff);
            note.Data.Reminders.Add(NoteReminder.Create(DateTime.Now.AddHours(1), recurring: true, now: DateTime.Now));

            oneOff.MarkFired(DateTime.Now);
            note.TriggerReminderAlert();
            UiThread.Pump();

            Assert.Equal(Visibility.Visible, filled.Visibility);
            note.DismissReminderAlert();
        });
    }

    [Fact]
    public void A_due_reminder_alerts_its_note_and_clicking_it_dismisses_the_alert()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            note.Data.Reminders.Add(new NoteReminder { TargetTime = DateTime.Now.AddMinutes(-1) });

            typeof(NoteManager).GetMethod("CheckReminders", NonPublic)!.Invoke(scope.Manager, null);
            UiThread.Pump();

            Assert.True(Field<bool>(note, "_isAlertFlashing"));
            Assert.True(note.Data.Reminders[0].HasFired);

            note.DismissReminderAlert();
            Assert.False(Field<bool>(note, "_isAlertFlashing"));
            Assert.True(Field<bool>(note, "_alertKeepOnTop"));
        });
    }

    [Fact]
    public void A_reminder_only_alerts_once()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            note.Data.Reminders.Add(new NoteReminder { TargetTime = DateTime.Now.AddMinutes(-1) });
            var check = typeof(NoteManager).GetMethod("CheckReminders", NonPublic)!;

            check.Invoke(scope.Manager, null);
            note.DismissReminderAlert();
            check.Invoke(scope.Manager, null);

            Assert.False(Field<bool>(note, "_isAlertFlashing"));
        });
    }

    private static ScrollBar? FindVerticalScrollBar(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollBar bar && bar.Orientation == Orientation.Vertical) return bar;
            var found = FindVerticalScrollBar(child);
            if (found != null) return found;
        }
        return null;
    }

    [Fact]
    public void The_note_scrollbar_is_a_vertical_pill()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote(string.Join("\n", Enumerable.Range(0, 80).Select(i => "line " + i)));
            note.UpdateLayout();
            UiThread.Pump();
            note.UpdateLayout();

            var bar = FindVerticalScrollBar(ManagerScope.EditorOf(note));
            Assert.NotNull(bar);
            var track = (Track)bar!.Template.FindName("PART_Track", bar);

            Assert.Equal(Orientation.Vertical, track.Orientation);
            Assert.True(track.Thumb.ActualWidth > 0);
            Assert.True(track.Thumb.ActualHeight > track.Thumb.ActualWidth * 2);
        });
    }

    [Fact]
    public void The_scrollbar_template_also_works_horizontally()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var bar = new ScrollBar
            {
                Orientation = Orientation.Horizontal,
                Style = (Style)note.FindResource("PillScrollBar"),
                Width = 100,
                Height = 10,
                Maximum = 100,
                ViewportSize = 30,
            };
            var host = new Window
            {
                Content = bar,
                Left = -5000,
                Top = 0,
                Width = 200,
                Height = 100,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };
            host.Show();
            UiThread.Pump();

            var track = (Track)bar.Template.FindName("PART_Track", bar);

            Assert.Equal(Orientation.Horizontal, track.Orientation);
            Assert.False(track.IsDirectionReversed);
            host.Close();
        });
    }
}
