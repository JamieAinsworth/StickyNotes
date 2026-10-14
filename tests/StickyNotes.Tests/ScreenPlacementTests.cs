using System.Collections.Generic;
using Xunit;

namespace StickyNotes.Tests;

public class ScreenPlacementTests
{
    private static readonly PixelRect Main = new(0, 0, 1920, 1080);
    private static readonly PixelRect Right = new(1920, 0, 3840, 1080);
    private static readonly PixelRect MainWork = new(0, 0, 1920, 1040);   // taskbar along the bottom

    private static PixelRect Header(int left, int top, int width = 280, int height = 32) =>
        new(left, top, left + width, top + height);

    [Fact]
    public void A_note_fully_on_a_monitor_is_reachable()
    {
        Assert.True(ScreenPlacement.IsReachable(Header(100, 100), new[] { Main }));
    }

    [Fact]
    public void A_note_on_a_monitor_that_has_been_unplugged_is_not_reachable()
    {
        var onSecondMonitor = Header(2500, 200);

        Assert.True(ScreenPlacement.IsReachable(onSecondMonitor, new[] { Main, Right }));
        Assert.False(ScreenPlacement.IsReachable(onSecondMonitor, new[] { Main }));
    }

    [Fact]
    public void A_note_on_a_monitor_to_the_left_or_above_is_also_caught()
    {
        Assert.False(ScreenPlacement.IsReachable(Header(-1500, 100), new[] { Main }));
        Assert.False(ScreenPlacement.IsReachable(Header(100, -900), new[] { Main }));
        Assert.False(ScreenPlacement.IsReachable(Header(100, 1500), new[] { Main }));
    }

    [Fact]
    public void A_note_hanging_mostly_off_the_edge_is_reachable_while_enough_of_its_header_shows()
    {
        // 100 px of the header still on screen: easy to grab and drag back.
        Assert.True(ScreenPlacement.IsReachable(Header(1820, 100), new[] { Main }));
        Assert.True(ScreenPlacement.IsReachable(Header(-180, 100), new[] { Main }));
    }

    [Fact]
    public void A_note_with_only_a_sliver_showing_is_not_reachable()
    {
        Assert.False(ScreenPlacement.IsReachable(Header(1900, 100), new[] { Main }));
        Assert.False(ScreenPlacement.IsReachable(Header(-250, 100), new[] { Main }));
    }

    [Fact]
    public void A_note_whose_header_is_mostly_above_the_screen_is_not_reachable()
    {
        Assert.False(ScreenPlacement.IsReachable(Header(100, -20), new[] { Main }));
        Assert.True(ScreenPlacement.IsReachable(Header(100, -10), new[] { Main }));
    }

    [Fact]
    public void A_note_in_the_gap_between_staggered_monitors_is_not_reachable()
    {
        // A small monitor on the right sits at the top; the area below it is inside the bounding box of both
        // monitors but isn't on either of them.
        var small = new PixelRect(1920, 0, 3200, 720);

        Assert.False(ScreenPlacement.IsReachable(Header(2400, 900), new[] { Main, small }));
        Assert.True(ScreenPlacement.IsReachable(Header(2400, 300), new[] { Main, small }));
    }

    [Fact]
    public void A_note_straddling_two_monitors_is_reachable()
    {
        Assert.True(ScreenPlacement.IsReachable(Header(1800, 100), new[] { Main, Right }));
    }

    [Fact]
    public void No_monitors_means_nothing_is_reachable()
    {
        Assert.False(ScreenPlacement.IsReachable(Header(100, 100), new List<PixelRect>()));
    }

    [Fact]
    public void A_rescued_note_goes_to_the_top_right_of_the_main_work_area()
    {
        var (x, y) = ScreenPlacement.Rescue(width: 280, height: 280, MainWork, slot: 0);

        Assert.Equal(1920 - 280 - 40, x);
        Assert.Equal(40, y);
    }

    [Fact]
    public void Several_rescued_notes_cascade_instead_of_stacking()
    {
        var first = ScreenPlacement.Rescue(280, 280, MainWork, 0);
        var second = ScreenPlacement.Rescue(280, 280, MainWork, 1);
        var third = ScreenPlacement.Rescue(280, 280, MainWork, 2);

        Assert.Equal((first.X - 32, first.Y + 32), second);
        Assert.Equal((first.X - 64, first.Y + 64), third);
    }

    [Fact]
    public void The_cascade_wraps_around_after_eight_notes()
    {
        Assert.Equal(ScreenPlacement.Rescue(280, 280, MainWork, 0), ScreenPlacement.Rescue(280, 280, MainWork, 8));
    }

    [Fact]
    public void A_rescued_note_stays_inside_the_work_area()
    {
        var area = new PixelRect(100, 50, 1100, 650);   // work area not at the origin

        for (int slot = 0; slot < 8; slot++)
        {
            var (x, y) = ScreenPlacement.Rescue(280, 400, area, slot);

            Assert.InRange(x, area.Left, area.Right - 280);
            Assert.InRange(y, area.Top, area.Bottom - 400);
        }
    }

    [Fact]
    public void A_note_bigger_than_the_work_area_is_pinned_to_its_top_left_corner()
    {
        var tiny = new PixelRect(0, 0, 200, 150);

        Assert.Equal((0, 0), ScreenPlacement.Rescue(500, 500, tiny, 3));
    }

    [Fact]
    public void Rectangles_report_their_size_and_overlap()
    {
        var overlap = new PixelRect(0, 0, 100, 100).Intersect(new PixelRect(60, 80, 300, 300));

        Assert.Equal(new PixelRect(60, 80, 100, 100), overlap);
        Assert.Equal(40, overlap.Width);
        Assert.Equal(20, overlap.Height);
        Assert.Equal(0, new PixelRect(0, 0, 10, 10).Intersect(new PixelRect(50, 50, 60, 60)).Width);
    }
}
