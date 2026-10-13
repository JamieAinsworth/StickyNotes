using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace StickyNotes;

public sealed class NoteManager
{
    private const double DefaultSize = NoteData.DefaultSize;

    private readonly List<NoteWindow> _windows = new();
    private readonly List<NoteData> _pendingRecreate = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _recreateTimer;
    private readonly DispatcherTimer _reminderTimer;
    private int _recreateAttempts;
    private bool _shuttingDown;
    private bool _reloading;

    /// <summary>True while windows are being closed for a reason other than the user deleting them.</summary>
    public bool AllowClose => _shuttingDown || _reloading;

    public NoteManager()
    {
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
                if (reminder.IsRecurring)
                {
                    // Daily recurring: trigger if current time >= reminder time-of-day and not already fired today.
                    bool firedToday = reminder.LastFiredDate.HasValue && reminder.LastFiredDate.Value.Date == now.Date;
                    if (!firedToday && now.TimeOfDay >= reminder.TargetTime.TimeOfDay)
                    {
                        reminder.LastFiredDate = now.Date;
                        changed = true;
                        window.TriggerReminderAlert();
                    }
                }
                else
                {
                    // Non-recurring: trigger once when target time is reached.
                    if (!reminder.HasFired && now >= reminder.TargetTime)
                    {
                        reminder.HasFired = true;
                        changed = true;
                        window.TriggerReminderAlert();
                    }
                }
            }
        }

        if (changed) RequestSave();
    }

    /// <summary>True while all notes are brought in front of other windows.</summary>
    public bool Raised { get; private set; }

    private bool _stickyRaise;

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
        return notes.Count;
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
        window.Activate();
        window.FocusEditor();
        RequestSave();
        return window;
    }

    private NoteWindow ShowNote(NoteData data)
    {
        var window = new NoteWindow(this, data);
        window.Closed += OnWindowClosed;
        _windows.Add(window);
        window.Show();
        return window;
    }

    public void DeleteNote(NoteWindow window)
    {
        window.IsDeleted = true;
        window.Close();
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
        _saveTimer.Stop();
        _recreateTimer.Stop();
        _reminderTimer.Stop();
        SaveNow();
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
