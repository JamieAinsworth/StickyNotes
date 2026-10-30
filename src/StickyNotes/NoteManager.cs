using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace StickyNotes;

public sealed class NoteManager
{
    private const double DefaultSize = NoteData.DefaultSize;

    private readonly List<NoteWindow> _windows = new();
    private readonly List<NoteData> _pendingRecreate = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _recreateTimer;
    private readonly DispatcherTimer _reminderTimer;
    private readonly DispatcherTimer _displayTimer;
    private int _recreateAttempts;
    private bool _shuttingDown;
    private bool _reloading;

    /// <summary>True while windows are being closed for a reason other than the user deleting them.</summary>
    public bool AllowClose => _shuttingDown || _reloading;

    public NoteManager()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        _recreateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _recreateTimer.Tick += (_, _) => TryRecreatePending();

        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reminderTimer.Tick += (_, _) => CheckReminders();
        _reminderTimer.Start();

        // Docking, undocking and Win+P each raise several display events in a burst; wait for them to settle.
        _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _displayTimer.Tick += (_, _) =>
        {
            _displayTimer.Stop();
            RescueOffScreenNotes();
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    // Raised on a system thread, so hop onto the UI thread before touching timers or windows.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (_shuttingDown) return;
            _displayTimer.Stop();
            _displayTimer.Start();
        });

    /// <summary>Moves any note that is no longer on a connected monitor onto the main one. Returns how many moved.</summary>
    public int RescueOffScreenNotes()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0) return 0;

        var main = System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
        return RescueOffScreenNotes(
            screens.Select(s => PixelRect.From(s.Bounds)).ToList(),
            PixelRect.From(main.WorkingArea));
    }

    internal int RescueOffScreenNotes(IReadOnlyList<PixelRect> monitors, PixelRect mainWorkArea)
    {
        int moved = 0;
        foreach (var window in _windows.ToList())
        {
            if (window.EnsureReachable(monitors, mainWorkArea, moved)) moved++;
        }
        return moved;
    }

    private void CheckReminders()
    {
        var now = DateTime.Now;
        bool changed = false;

        foreach (var window in _windows.ToList())
        {
            var note = window.Data;
            if (note.Reminders == null || note.Reminders.Count == 0) continue;

            foreach (var reminder in note.Reminders)
            {
                if (!reminder.IsDue(now)) continue;

                reminder.MarkFired(now);
                changed = true;
                window.TriggerReminderAlert();
            }
        }

        if (changed) RequestSave();
    }

    /// <summary>True while all notes are brought in front of other windows.</summary>
    public bool Raised { get; private set; }

    private bool _stickyRaise;

    /// <summary>
    /// The note being activated right now (clicked, created or focused by the app). Notes are owned by the desktop
    /// window, and Windows raises a whole owned group when one member activates; the other notes use this to stay put.
    /// </summary>
    internal NoteWindow? ActivatingNote { get; private set; }

    internal void BeginActivating(NoteWindow window)
    {
        ActivatingNote = window;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (ActivatingNote == window) ActivatingNote = null;
        });
    }

    /// <summary>
    /// Brings every note in front of other windows. A temporary raise (tray double-click) drops back to the
    /// desktop once focus leaves the notes; a sticky one (hotkey) stays until toggled off.
    /// </summary>
    public void BringForward(bool sticky = false)
    {
        Raised = true;
        _stickyRaise = sticky;
        foreach (var window in _windows)
        {
            window.ApplyZOrder();
            window.OnManagerRaised();
        }
        if (_windows.Count == 0 || sticky) return;

        _windows[0].Activate();
        // If Windows refused to give focus, no deactivation would ever happen, so keep them raised until toggled.
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (Raised && !_stickyRaise && !_windows.Any(w => w.IsActive)) _stickyRaise = true;
        });
    }

    /// <summary>Called when a note loses focus; ends a temporary raise once no note has focus.</summary>
    public void NoteDeactivated()
    {
        if (!Raised || _stickyRaise) return;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (Raised && !_stickyRaise && !_windows.Any(w => w.IsActive)) SendToDesktop();
        });
    }

    /// <summary>Puts every note back on the desktop layer.</summary>
    public void SendToDesktop()
    {
        Raised = false;
        _stickyRaise = false;
        foreach (var window in _windows)
        {
            window.ApplyZOrder();
            window.OnManagerLowered();
        }
    }

    public void ToggleForward()
    {
        if (Raised) SendToDesktop();
        else BringForward(sticky: true);
    }

    /// <summary>Shows the saved notes and returns how many there were.</summary>
    public int LoadAndShow()
    {
        var notes = NoteStore.Load();
        foreach (var note in notes)
        {
            EnsureOnScreen(note);
            ShowNote(note);
        }
        DeleteUnusedImages();
        return notes.Count;
    }

    /// <summary>
    /// Deletes image files no open or recently deleted note uses (see <see cref="ImageStore.DeleteUnused"/>).
    /// Images removed from a note while the app runs could still come back with Ctrl+Z, so this only runs at
    /// start-up and exit.
    /// </summary>
    private void DeleteUnusedImages()
    {
        try
        {
            var inUse = _windows.SelectMany(w => ImageEditor.NamesIn(w.EditorDocument))
                .Concat(_pendingRecreate.Concat(_deletedNotes.Select(d => d.Data)).SelectMany(d => ImageStore.NamesIn(d.Content)));
            ImageStore.DeleteUnused(AppSettings.Current.ResolvedDataFolder, inUse);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Failed to clean up images: " + ex);
        }
    }

    public NoteWindow CreateNote(NoteWindow? from = null)
    {
        var data = new NoteData();

        if (from != null)
        {
            data.Color = from.Data.Color;
            data.Width = from.ActualWidth;
            data.Height = from.ActualHeight;
            data.Left = from.Left + 32;
            data.Top = from.Top + 32;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            int offset = (_windows.Count % 8) * 32;
            data.Left = area.Right - DefaultSize - 40 - offset;
            data.Top = area.Top + 40 + offset;
        }

        EnsureOnScreen(data);
        var window = ShowNote(data);
        window.ActivateNote();
        window.FocusEditor();
        RequestSave();
        return window;
    }

    private NoteWindow ShowNote(NoteData data)
    {
        var window = new NoteWindow(this, data);
        window.Closed += OnWindowClosed;
        _windows.Add(window);
        // Showing a note activates it, which would otherwise pull every other note up with it.
        BeginActivating(window);
        window.Show();
        return window;
    }

    private const int MaxUndoableDeletes = 10;

    private readonly List<(NoteData Data, DateTime DeletedAt)> _deletedNotes = new();

    /// <summary>True when a deleted note can be brought back.</summary>
    public bool CanUndoDelete => _deletedNotes.Count > 0;

    public void DeleteNote(NoteWindow window)
    {
        var data = window.CaptureData();
        _deletedNotes.Add((data, DateTime.Now));
        if (_deletedNotes.Count > MaxUndoableDeletes) _deletedNotes.RemoveAt(0);

        // Closing the active note makes Windows activate another window, often a sibling note, which would come
        // forward over other apps. Handing focus back to the app that was in use first keeps every note where it is.
        if (window.IsActive && !Raised)
        {
            var previous = NativeMethods.TopOtherAppWindow();
            if (previous != IntPtr.Zero) NativeMethods.SetForegroundWindow(previous);
        }
        window.IsDeleted = true;
        window.Close();
    }

    /// <summary>Brings back the most recently deleted note, where it was, with its content and reminders.</summary>
    public NoteWindow? UndoDelete()
    {
        if (_deletedNotes.Count == 0) return null;
        return UndoDelete(_deletedNotes[^1].Data);
    }

    /// <summary>Deleted notes that can still be restored, newest first.</summary>
    public IReadOnlyList<DeletedNote> RecentlyDeleted() =>
        _deletedNotes.AsEnumerable().Reverse().Select(d => new DeletedNote(d.Data, d.DeletedAt)).ToList();

    /// <summary>Brings back a specific deleted note, where it was, with its content and reminders.</summary>
    public NoteWindow? UndoDelete(NoteData data)
    {
        int index = _deletedNotes.FindIndex(d => ReferenceEquals(d.Data, data));
        if (index < 0) return null;
        _deletedNotes.RemoveAt(index);

        EnsureOnScreen(data);
        var window = ShowNote(data);
        window.ActivateNote();
        window.FocusEditor();
        RequestSave();
        return window;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not NoteWindow window) return;
        _windows.Remove(window);
        if (AllowClose) return;

        if (!window.IsDeleted)
        {
            // Not closed by the user: the desktop window that owns the notes went away
            // (e.g. Explorer restarted). Bring the note back once the desktop is available.
            _pendingRecreate.Add(window.CaptureData());
            _recreateAttempts = 0;
            _recreateTimer.Start();
        }

        RequestSave();
    }

    private void TryRecreatePending()
    {
        _recreateAttempts++;
        if (NativeMethods.GetDesktopProgman() == IntPtr.Zero && _recreateAttempts < 30) return;

        _recreateTimer.Stop();
        var notes = _pendingRecreate.ToList();
        _pendingRecreate.Clear();
        foreach (var note in notes) ShowNote(note);
        RequestSave();
    }

    public void RequestSave()
    {
        if (_shuttingDown) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        try
        {
            var notes = _windows.Select(w => w.CaptureData()).Concat(_pendingRecreate);
            NoteStore.Save(notes);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Failed to save notes: " + ex);
        }
    }

    /// <summary>Closes all notes and shows the ones saved in the current data folder.</summary>
    public void Reload()
    {
        _saveTimer.Stop();
        _recreateTimer.Stop();
        _pendingRecreate.Clear();

        // The deleted notes belong to the notes being closed; restoring one into the new set would mix them up.
        _deletedNotes.Clear();
        _reloading = true;
        try
        {
            CloseAllWindows();
        }
        finally
        {
            _reloading = false;
        }
        LoadAndShow();
    }

    public void Shutdown()
    {
        if (_shuttingDown) return;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _saveTimer.Stop();
        _recreateTimer.Stop();
        _reminderTimer.Stop();
        _displayTimer.Stop();
        SaveNow();
        // Deleted notes can't be restored after exit, so their images are no longer needed.
        _deletedNotes.Clear();
        DeleteUnusedImages();
        _shuttingDown = true;
        CloseAllWindows();
    }

    private void CloseAllWindows()
    {
        foreach (var window in _windows.ToList()) window.Close();
        _windows.Clear();
    }

    private static void EnsureOnScreen(NoteData data)
    {
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        if (double.IsNaN(data.Width) || data.Width < 150) data.Width = DefaultSize;
        if (double.IsNaN(data.Height) || data.Height < 120) data.Height = DefaultSize;

        // Require at least part of the header to be reachable so the note can be dragged back.
        bool visible = data.Left + 60 <= vRight && data.Left + data.Width - 60 >= vLeft &&
                       data.Top >= vTop - 10 && data.Top + 30 <= vBottom;
        if (!visible || double.IsNaN(data.Left) || double.IsNaN(data.Top))
        {
            var area = SystemParameters.WorkArea;
            data.Left = area.Left + (area.Width - data.Width) / 2;
            data.Top = area.Top + (area.Height - data.Height) / 2;
        }
    }
}
