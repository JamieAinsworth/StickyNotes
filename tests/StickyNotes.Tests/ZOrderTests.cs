using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Xunit;

namespace StickyNotes.Tests;

[Collection(NotesCollection.Name)]
public class ZOrderTests
{
    private const uint GW_HWNDPREV = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private static IntPtr Hwnd(Window window) => new WindowInteropHelper(window).Handle;

    private static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        for (var h = GetWindow(lower, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
            if (h == upper) return true;
        return false;
    }

    [Fact]
    public void Other_notes_hold_their_place_while_one_activates()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var other = scope.CreateNote("other");
            var activating = scope.CreateNote("activating");
            UiThread.Pump();

            scope.Manager.BeginActivating(activating);

            Assert.Equal(!other.IsActive, other.ShouldHoldZOrder(IntPtr.Zero));
            Assert.False(activating.ShouldHoldZOrder(IntPtr.Zero));
            Assert.False(other.ShouldHoldZOrder(NativeMethods.HWND_BOTTOM));
            Assert.False(other.ShouldHoldZOrder(NativeMethods.HWND_TOPMOST));
        });
    }

    [Fact]
    public void Nothing_is_held_once_activation_is_over_or_notes_are_raised()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var other = scope.CreateNote("other");
            var activating = scope.CreateNote("activating");

            scope.Manager.BeginActivating(activating);
            UiThread.Pump();
            Assert.Null(scope.Manager.ActivatingNote);
            Assert.False(other.ShouldHoldZOrder(IntPtr.Zero));

            scope.Manager.BringForward(sticky: true);
            scope.Manager.BeginActivating(activating);
            Assert.False(other.ShouldHoldZOrder(IntPtr.Zero));
            scope.Manager.SendToDesktop();
        });
    }

    [Fact]
    public void A_sibling_note_is_not_pulled_up_when_another_note_activates()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var other = scope.CreateNote("other");
            var activating = scope.CreateNote("activating");
            var probe = new Window { Width = 50, Height = 50, ShowActivated = false, ShowInTaskbar = false };
            probe.Show();
            try
            {
                other.ApplyZOrder();
                UiThread.Pump();
                if (other.IsActive) return; // Focus landed on the background note; the hold rightly doesn't apply.
                Assert.True(IsAbove(Hwnd(probe), Hwnd(other)));

                // What Windows does to the rest of the desktop-owned group when one note activates.
                scope.Manager.BeginActivating(activating);
                NativeMethods.SetWindowPos(Hwnd(other), IntPtr.Zero, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

                Assert.True(IsAbove(Hwnd(probe), Hwnd(other)));
            }
            finally
            {
                probe.Close();
            }
        });
    }
}
