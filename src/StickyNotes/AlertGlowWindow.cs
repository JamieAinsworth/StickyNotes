using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace StickyNotes;

/// <summary>
/// A click-through, transparent window kept just behind a note that draws a pulsing white glow
/// (with a hint of the note's colour) around the note's outside edge.
/// </summary>
internal sealed class AlertGlowWindow : Window
{
    private const double Reach = 44;

    private readonly NoteWindow _note;
    private IntPtr _hwnd;

    public AlertGlowWindow(NoteWindow note, Color tone)
    {
        _note = note;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;

        // The note is drawn over these blocks, so only their blurred shadows are visible around the edge.
        var toneGlow = new Border
        {
            Margin = new Thickness(Reach),
            Background = new SolidColorBrush(tone),
            Effect = new DropShadowEffect { Color = tone, BlurRadius = 42, ShadowDepth = 0, Opacity = 1 },
        };
        var whiteGlow = new Border
        {
            Margin = new Thickness(Reach + 1),
            Background = Brushes.White,
            Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 22, ShadowDepth = 0, Opacity = 1 },
        };

        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(toneGlow);
        root.Children.Add(whiteGlow);
        Content = root;

        root.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(750))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        long exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        Follow();
    }

    /// <summary>Matches the note's bounds and sits directly behind it in the z-order.</summary>
    public void Follow()
    {
        if (double.IsNaN(_note.Left) || double.IsNaN(_note.Top)) return;

        Left = _note.Left - Reach;
        Top = _note.Top - Reach;
        Width = _note.ActualWidth + Reach * 2;
        Height = _note.ActualHeight + Reach * 2;

        var noteHwnd = new WindowInteropHelper(_note).Handle;
        if (_hwnd != IntPtr.Zero && noteHwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(_hwnd, noteHwnd, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }
}
