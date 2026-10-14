using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace StickyNotes.Tests;

[Collection(NotesCollection.Name)]
public class DisplayChangeTests
{
    private static PixelRect WindowRect(NoteWindow note)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(note).Handle;
        Assert.True(NativeMethods.GetWindowRect(handle, out var rect));
        return new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static PixelRect Primary() =>
        PixelRect.From(System.Windows.Forms.Screen.PrimaryScreen!.Bounds);

    [Fact]
    public void A_note_left_on_a_monitor_that_is_gone_is_brought_to_the_main_screen()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("stranded");
            note.Left = 20000;   // far beyond any real monitor: where the second screen used to be
            UiThread.Pump();
            Assert.False(ScreenPlacement.IsReachable(WindowRect(note), System.Windows.Forms.Screen.AllScreens.Select(s => PixelRect.From(s.Bounds)).ToList()));

            int moved = scope.Manager.RescueOffScreenNotes();
            UiThread.Pump();

            Assert.Equal(1, moved);
            var rect = WindowRect(note);
            var work = PixelRect.From(System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea);
            Assert.InRange(rect.Left, work.Left, work.Right - rect.Width);
            Assert.InRange(rect.Top, work.Top, work.Bottom - rect.Height);
            Assert.InRange(note.Left, -1, 20000 - 1);   // the WPF position followed the move
        });
    }

    [Fact]
    public void Notes_that_are_still_on_screen_are_left_where_they_are()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("fine");
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            note.Left = work.Left + 100;
            note.Top = work.Top + 100;
            UiThread.Pump();
            var before = WindowRect(note);

            int moved = scope.Manager.RescueOffScreenNotes();
            UiThread.Pump();

            Assert.Equal(0, moved);
            Assert.Equal(before, WindowRect(note));
        });
    }

    [Fact]
    public void Unplugging_a_monitor_is_simulated_by_removing_it_from_the_monitor_list()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("on the old second monitor");
            UiThread.Pump();
            var before = WindowRect(note);

            // Pretend the only monitor now is one somewhere else entirely, so the note is on a disconnected screen.
            var remaining = new PixelRect(10000, 0, 11920, 1080);
            var remainingWork = new PixelRect(10000, 0, 11920, 1040);

            int moved = scope.Manager.RescueOffScreenNotes(new List<PixelRect> { remaining }, remainingWork);
            UiThread.Pump();

            Assert.Equal(1, moved);
            var expected = ScreenPlacement.Rescue(before.Width, before.Height, remainingWork, slot: 0);
            var after = WindowRect(note);
            Assert.Equal(expected, (after.Left, after.Top));
            Assert.Equal(before.Width, after.Width);     // size is kept
            Assert.Equal(before.Height, after.Height);
        });
    }

    [Fact]
    public void Several_stranded_notes_land_in_a_cascade_not_on_top_of_each_other()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var notes = Enumerable.Range(0, 3).Select(i => scope.CreateNote("note " + i)).ToList();
            UiThread.Pump();
            var remaining = new PixelRect(10000, 0, 11920, 1080);

            int moved = scope.Manager.RescueOffScreenNotes(new List<PixelRect> { remaining }, remaining);
            UiThread.Pump();

            Assert.Equal(3, moved);
            var positions = notes.Select(n => (WindowRect(n).Left, WindowRect(n).Top)).Distinct().ToList();
            Assert.Equal(3, positions.Count);
        });
    }

    [Fact]
    public void A_stranded_collapsed_note_is_rescued_too()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("collapsed");
            typeof(NoteWindow).GetMethod("ApplyCollapsed", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(note, new object[] { true, true });
            note.Left = 20000;
            UiThread.Pump();

            Assert.Equal(1, scope.Manager.RescueOffScreenNotes());
        });
    }

    [Fact]
    public void A_display_change_event_rescues_stranded_notes_after_it_settles()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("docked laptop");
            note.Left = 20000;
            UiThread.Pump();

            // What Windows raises when a monitor is unplugged; several events arrive in a burst.
            var handler = typeof(NoteManager).GetMethod("OnDisplaySettingsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
            for (int i = 0; i < 3; i++) handler.Invoke(scope.Manager, new object?[] { null, System.EventArgs.Empty });

            UiThread.Wait(1500);

            var rect = WindowRect(note);
            Assert.True(ScreenPlacement.IsReachable(
                new PixelRect(rect.Left, rect.Top, rect.Right, rect.Top + 40),
                System.Windows.Forms.Screen.AllScreens.Select(s => PixelRect.From(s.Bounds)).ToList()));
        });
    }

    [Fact]
    public void Nothing_happens_after_shutdown()
    {
        UiThread.Run(() =>
        {
            while (UiThread.Unhandled.TryDequeue(out _)) { }
            var scope = new ManagerScope();
            var note = scope.CreateNote("closing");
            scope.Manager.Shutdown();

            var handler = typeof(NoteManager).GetMethod("OnDisplaySettingsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
            handler.Invoke(scope.Manager, new object?[] { null, System.EventArgs.Empty });
            UiThread.Wait(1200);

            Assert.Empty(UiThread.Unhandled);
            scope.Dispose();
        });
    }
}
