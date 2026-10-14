using System;
using System.Collections.Generic;
using System.Linq;

namespace StickyNotes;

/// <summary>A rectangle in physical screen pixels (not DPI-scaled units).</summary>
internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);

    public PixelRect Intersect(PixelRect other) =>
        new(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));

    public static PixelRect From(System.Drawing.Rectangle rectangle) =>
        new(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
}

/// <summary>Works out whether a note can still be reached on the connected monitors, and where to move it if not.</summary>
internal static class ScreenPlacement
{
    private const int MinVisibleWidth = 60;
    private const int MinVisibleHeight = 20;
    private const int Margin = 40;
    private const int CascadeStep = 32;
    private const int CascadeSlots = 8;

    /// <summary>
    /// True when enough of a note's header strip lies on some monitor to see it and drag it. Checking each monitor
    /// (rather than the bounding box of all of them) also catches notes in the gaps between monitors of
    /// different sizes or in a staggered arrangement.
    /// </summary>
    public static bool IsReachable(PixelRect header, IReadOnlyList<PixelRect> monitors) =>
        monitors.Any(monitor =>
        {
            var visible = header.Intersect(monitor);
            return visible.Width >= MinVisibleWidth && visible.Height >= MinVisibleHeight;
        });

    /// <summary>
    /// Where to put a note of the given size on the main monitor's work area: the top-right (where new notes
    /// appear), shifted down and left for each <paramref name="slot"/> so several rescued notes don't stack up.
    /// </summary>
    public static (int X, int Y) Rescue(int width, int height, PixelRect workArea, int slot)
    {
        int offset = (slot % CascadeSlots) * CascadeStep;
        int x = workArea.Right - width - Margin - offset;
        int y = workArea.Top + Margin + offset;

        // Never past the edges; a note bigger than the area is pinned to its top-left corner.
        x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - width));
        y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - height));
        return (x, y);
    }
}
