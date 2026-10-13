using System;
using System.Linq;
using Xunit;

namespace StickyNotes.Tests;

[Collection(NotesCollection.Name)]
public class NoteManagerTests
{
    [Fact]
    public void Nothing_can_be_undone_at_first()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();

            Assert.False(scope.Manager.CanUndoDelete);
            Assert.Null(scope.Manager.UndoDelete());
            Assert.Empty(scope.Manager.RecentlyDeleted());
        });
    }

    [Fact]
    public void Undo_restores_text_position_colour_and_reminders()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("buy milk");
            note.Left = 300;
            note.Top = 200;
            UiThread.Pump();
            note.Data.Color = "Pink";
            note.Data.Reminders.Add(NoteReminder.Create(DateTime.Now.AddHours(2), recurring: true, now: DateTime.Now));

            scope.Manager.DeleteNote(note);
            UiThread.Pump();
            Assert.Empty(scope.OpenNotes());
            Assert.True(scope.Manager.CanUndoDelete);

            var restored = scope.Manager.UndoDelete();
            UiThread.Pump();

            Assert.NotNull(restored);
            Assert.Single(scope.OpenNotes());
            Assert.Equal("buy milk", ManagerScope.TextOf(restored!));
            Assert.InRange(restored!.Left, 299, 301);
            Assert.InRange(restored.Top, 199, 201);
            Assert.Equal("Pink", restored.Data.Color);
            Assert.Single(restored.Data.Reminders);
            Assert.False(scope.Manager.CanUndoDelete);
        });
    }

    [Fact]
    public void An_empty_note_can_be_undone_too()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();

            scope.Manager.DeleteNote(note);
            UiThread.Pump();

            Assert.NotNull(scope.Manager.UndoDelete());
            Assert.Single(scope.OpenNotes());
        });
    }

    [Fact]
    public void The_most_recent_deletion_comes_back_first()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var first = scope.CreateNote("first");
            var second = scope.CreateNote("second");
            scope.Manager.DeleteNote(first);
            scope.Manager.DeleteNote(second);
            UiThread.Pump();

            var one = scope.Manager.UndoDelete();
            var two = scope.Manager.UndoDelete();

            Assert.Equal("second", ManagerScope.TextOf(one!));
            Assert.Equal("first", ManagerScope.TextOf(two!));
        });
    }

    [Fact]
    public void Recently_deleted_lists_newest_first_and_a_chosen_note_can_be_restored()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var a = scope.CreateNote("Alpha note");
            var b = scope.CreateNote("Fish & chips");
            var c = scope.CreateNote();
            scope.Manager.DeleteNote(a);
            scope.Manager.DeleteNote(b);
            scope.Manager.DeleteNote(c);
            UiThread.Pump();

            var recent = scope.Manager.RecentlyDeleted();
            Assert.Equal(new[] { "(empty note)", "Fish & chips", "Alpha note" }, recent.Select(r => r.Title).ToArray());

            var restored = scope.Manager.UndoDelete(recent[2].Data);
            UiThread.Pump();

            Assert.Equal("Alpha note", ManagerScope.TextOf(restored!));
            Assert.Equal(2, scope.Manager.RecentlyDeleted().Count);
            Assert.Null(scope.Manager.UndoDelete(new NoteData()));
        });
    }

    [Fact]
    public void Only_the_last_ten_deletions_are_kept()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            for (int i = 0; i < 12; i++) scope.Manager.DeleteNote(scope.CreateNote("n" + i));
            UiThread.Pump();

            Assert.Equal(10, scope.Manager.RecentlyDeleted().Count);
            Assert.Equal("n11", scope.Manager.RecentlyDeleted()[0].Title);
            Assert.Equal("n2", scope.Manager.RecentlyDeleted()[9].Title);
        });
    }

    [Fact]
    public void Switching_notes_folder_clears_the_deleted_notes_history()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            scope.Manager.DeleteNote(scope.CreateNote("from the old folder"));
            UiThread.Pump();
            Assert.True(scope.Manager.CanUndoDelete);

            scope.Manager.Reload();

            Assert.False(scope.Manager.CanUndoDelete);
            Assert.Empty(scope.Manager.RecentlyDeleted());
        });
    }

    [Fact]
    public void A_restored_note_is_saved_to_the_notes_file()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            scope.Manager.DeleteNote(scope.CreateNote("keep this"));
            scope.Manager.UndoDelete();
            UiThread.Pump();

            scope.Manager.SaveNow();

            var saved = NoteStore.Load();
            Assert.Contains(saved, n => n.PlainText == "keep this");
        });
    }

    [Fact]
    public void Notes_round_trip_through_the_notes_file_with_collapse_state_and_reminders()
    {
        var due = new DateTime(2026, 10, 13, 9, 30, 0);
        NoteStore.Save(new[]
        {
            new NoteData
            {
                PlainText = "saved",
                IsCollapsed = true,
                Reminders =
                {
                    new NoteReminder { TargetTime = due, IsRecurring = true, LastFiredDate = due.Date },
                    new NoteReminder { TargetTime = due.AddDays(1), HasFired = true },
                },
            },
        });

        var loaded = Assert.Single(NoteStore.Load());

        Assert.Equal("saved", loaded.PlainText);
        Assert.True(loaded.IsCollapsed);
        Assert.Equal(2, loaded.Reminders.Count);
        Assert.True(loaded.Reminders[0].IsRecurring);
        Assert.Equal(due, loaded.Reminders[0].TargetTime);
        Assert.Equal(due.Date, loaded.Reminders[0].LastFiredDate);
        Assert.True(loaded.Reminders[1].HasFired);
    }

    [Fact]
    public void Notes_saved_before_collapse_and_reminders_existed_still_load()
    {
        System.IO.File.WriteAllText(NoteStore.FilePathIn(AppSettings.Current.ResolvedDataFolder),
            "[{\"Id\":\"abc\",\"Left\":10,\"Top\":20,\"Width\":280,\"Height\":280,\"Color\":\"Yellow\",\"Pinned\":false,\"PlainText\":\"old note\"}]");

        var loaded = Assert.Single(NoteStore.Load());

        Assert.Equal("old note", loaded.PlainText);
        Assert.False(loaded.IsCollapsed);
        Assert.Empty(loaded.Reminders);
    }
}
