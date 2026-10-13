using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace StickyNotes;

public partial class App : Application
{
    private const string MutexName = @"Local\StickyNotes_SingleInstance";
    private const string NewNoteEventName = @"Local\StickyNotes_NewNote";

    /// <summary>Start quietly in the tray (used when starting with Windows).</summary>
    public const string BackgroundArg = "--background";

    private Mutex? _mutex;
    private EventWaitHandle? _newNoteSignal;
    private NoteManager? _manager;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkey;
    private SetupWindow? _settingsWindow;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        // When hosted by dotnet.exe (a console program), Windows may have opened a console window for us.
        // Detach from it; a console created just for this process then closes. No-op for StickyNotes.exe.
        NativeMethods.FreeConsole();

        base.OnStartup(e);

        bool background = HasArg(e.Args, BackgroundArg);

        // Avoid migrating data while an older version can still save to its original folder.
        if (Mutex.TryOpenExisting(@"Local\RhyhoStickyNotes_SingleInstance", out var legacyMutex))
        {
            legacyMutex.Dispose();
            MessageBox.Show("An older version of Sticky Notes is still running. Exit it from the tray menu, " +
                "then launch this version again to migrate your data.", "Sticky Notes",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool isFirstInstance);
        _newNoteSignal = new EventWaitHandle(false, EventResetMode.AutoReset, NewNoteEventName);

        if (!isFirstInstance)
        {
            // Already running: ask the existing instance to open a new note instead
            // (unless this is a quiet sign-in launch, which has nothing to add).
            if (!background) _newNoteSignal.Set();
            _newNoteSignal.Dispose();
            _newNoteSignal = null;
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        bool hasSettings;
        try
        {
            hasSettings = AppSettings.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't migrate your existing notes and settings:\n" + ex.Message,
                "Sticky Notes", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApp();
            return;
        }

        if (!hasSettings)
        {
            var setup = new SetupWindow(firstRun: true, saveCurrentNotes: null);
            setup.ShowDialog();
            if (!setup.Saved)
            {
                // Closed without choosing: keep the defaults so setup isn't shown every launch.
                try { AppSettings.Save(AppSettings.Current); } catch { }
            }
        }

        if (!EnsureDataFolderAvailable())
        {
            ExitApp();
            return;
        }

        StartupManager.RefreshIfEnabled();

        _manager = new NoteManager();
        _tray = new TrayIcon(_manager, OpenSettings, ExitApp);
        _hotkey = new HotkeyManager();
        bool toggleOk = _hotkey.Register(HotkeyManager.ToggleNotesId, AppSettings.Current.ToggleHotkey, () => _manager?.ToggleForward());
        bool newNoteOk = _hotkey.Register(HotkeyManager.NewNoteId, AppSettings.Current.NewNoteHotkey, () => _manager?.CreateNote());
        if (!toggleOk || !newNoteOk)
        {
            // A balloon rather than a dialog, so a quiet start with Windows isn't interrupted.
            _tray.ShowNotice("Sticky Notes",
                "A keyboard shortcut couldn't be registered; another app may be using it. Choose a different one in Settings.");
        }
        int shown = _manager.LoadAndShow();

        // Opened by hand with no notes: start with one so something visibly happens.
        // With --background (starting with Windows), just sit in the tray; use the tray icon's "New note" to add one.
        if (shown == 0 && !background) _manager.CreateNote();

        var listener = new Thread(ListenForNewNoteRequests) { IsBackground = true, Name = "NewNoteListener" };
        listener.Start();

        SessionEnding += (_, _) => _manager.SaveNow();
    }

    private static bool HasArg(string[] args, string name) =>
        Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Command line that starts this app, e.g. "C:\...\dotnet.exe" "C:\...\StickyNotes.dll".</summary>
    public static string LaunchCommand
    {
        get
        {
            string process = Environment.ProcessPath ?? "dotnet";
            string dll = Path.Combine(AppContext.BaseDirectory, "StickyNotes.dll");
            return Path.GetFileName(process).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                ? $"\"{process}\" \"{dll}\""
                : $"\"{process}\"";
        }
    }

    private void ListenForNewNoteRequests()
    {
        while (!_exiting && _newNoteSignal != null)
        {
            try
            {
                _newNoteSignal.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (_exiting) return;
            Dispatcher.BeginInvoke(() => _manager?.CreateNote());
        }
    }

    /// <summary>
    /// A custom notes folder may be on a drive that isn't connected yet. Starting anyway would show
    /// an empty desktop and could later overwrite the real notes, so ask the user what to do.
    /// </summary>
    private static bool EnsureDataFolderAvailable()
    {
        while (AppSettings.Current.UsesCustomFolder && !Directory.Exists(AppSettings.Current.ResolvedDataFolder))
        {
            var answer = MessageBox.Show(
                $"Your notes folder can't be found:\n{AppSettings.Current.ResolvedDataFolder}\n\n" +
                "It may be on a drive that isn't connected.\n\n" +
                "Yes: try again\nNo: choose a different folder\nCancel: exit Sticky Notes",
                "Sticky Notes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.No)
            {
                var setup = new SetupWindow(firstRun: false, saveCurrentNotes: null);
                setup.ShowDialog();
                if (setup.Saved) return true;
            }
        }
        return true;
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SetupWindow(firstRun: false, saveCurrentNotes: () => _manager?.SaveNow());
        _settingsWindow.Closed += (_, _) =>
        {
            if (_settingsWindow.Saved)
            {
                _tray?.RefreshColour();

                // Release both first: a swapped pair would otherwise clash with the other shortcut's old registration.
                _hotkey?.Unregister(HotkeyManager.ToggleNotesId);
                _hotkey?.Unregister(HotkeyManager.NewNoteId);
                bool toggleOk = _hotkey?.Register(HotkeyManager.ToggleNotesId, AppSettings.Current.ToggleHotkey, () => _manager?.ToggleForward()) ?? true;
                bool newNoteOk = _hotkey?.Register(HotkeyManager.NewNoteId, AppSettings.Current.NewNoteHotkey, () => _manager?.CreateNote()) ?? true;
                if (!toggleOk || !newNoteOk)
                    MessageBox.Show("One or more hotkeys couldn't be registered; they may be in use by another app. Choose a different combination in Settings.",
                        "Sticky Notes", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (_settingsWindow.ReloadNotes) _manager?.Reload();
            _settingsWindow = null;
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _settingsWindow?.Close();
        _manager?.Shutdown();
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _manager?.Shutdown();
        _tray?.Dispose();
        _hotkey?.Dispose();
        _newNoteSignal?.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}
