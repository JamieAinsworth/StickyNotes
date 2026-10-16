using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace StickyNotes;

/// <summary>First-run setup, also reused as the Settings window from the tray.</summary>
public partial class SetupWindow : Window
{
    private readonly bool _firstRun;
    private readonly Action? _saveCurrentNotes;
    private string _selectedColour;

    /// <summary>True once the user saved their choices.</summary>
    public bool Saved { get; private set; }

    /// <summary>True when the app switched to a folder that already had notes, so they need loading.</summary>
    public bool ReloadNotes { get; private set; }

    /// <param name="firstRun">Shows the welcome text.</param>
    /// <param name="saveCurrentNotes">Flushes open notes to disk before they're moved to a new folder.</param>
    public SetupWindow(bool firstRun, Action? saveCurrentNotes)
    {
        InitializeComponent();
        _firstRun = firstRun;
        _saveCurrentNotes = saveCurrentNotes;

        var settings = AppSettings.Current;
        _selectedColour = NoteColors.Get(settings.DefaultColor).Name;
        ColourList.ItemsSource = NoteColors.All;
        ColourName.Text = _selectedColour;

        FolderBox.Text = settings.ResolvedDataFolder;
        HotkeyBox.Text = settings.ToggleHotkey ?? string.Empty;
        NewNoteHotkeyBox.Text = settings.NewNoteHotkey ?? string.Empty;

        bool startup = false;
        try { startup = StartupManager.IsEnabled; } catch { }
        StartupYes.IsChecked = startup;
        StartupNo.IsChecked = !startup;

        if (!firstRun)
        {
            Title = "Sticky Notes settings";
            Heading.Text = "Settings";
            Intro.Text = "Changing the default colour only affects new notes.";
            SaveButton.Content = "Save";
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e) =>
        HandleHotkeyKeyDown(HotkeyBox, e);

    private void NewNoteHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e) =>
        HandleHotkeyKeyDown(NewNoteHotkeyBox, e);

    private void HandleHotkeyKeyDown(TextBox targetBox, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete or Key.Escape)
        {
            targetBox.Text = string.Empty;
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None or Key.DeadCharProcessed or Key.ImeProcessed)
            return;
        if (modifiers == ModifierKeys.None) return;

        targetBox.Text = HotkeyManager.Format(modifiers, key);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e) => HotkeyBox.Text = string.Empty;

    private void ClearNewNoteHotkey_Click(object sender, RoutedEventArgs e) => NewNoteHotkeyBox.Text = string.Empty;

    private void Swatch_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: NoteColor colour } button)
            button.IsChecked = colour.Name == _selectedColour;
    }

    private void Swatch_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { DataContext: NoteColor colour }) return;
        _selectedColour = colour.Name;
        ColourName.Text = colour.Name;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save your notes" };
        var current = ExpandPath(FolderBox.Text);
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void UseDefault_Click(object sender, RoutedEventArgs e) => FolderBox.Text = AppSettings.DefaultDataFolder;

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool isDefault;
        try { isDefault = AppSettings.IsSameFolder(ExpandPath(FolderBox.Text), AppSettings.DefaultDataFolder); }
        catch { isDefault = false; }

        FolderHint.Text = isDefault ? "This is the default location." : "Default: " + AppSettings.DefaultDataFolder;
        UseDefaultButton.Visibility = isDefault ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (HotkeyManager.AreSame(HotkeyBox.Text, NewNoteHotkeyBox.Text))
        {
            ShowError("The show/hide shortcut and the new note shortcut can't be the same. Choose a different combination for one of them.");
            return;
        }

        string folder = ExpandPath(FolderBox.Text);
        if (folder.Length == 0) folder = AppSettings.DefaultDataFolder;
        if (!Path.IsPathFullyQualified(folder))
        {
            ShowError("Please enter a full folder path, for example C:\\Users\\you\\Documents\\Notes.");
            return;
        }

        try
        {
            folder = Path.GetFullPath(folder);
            NoteStore.EnsureWritable(folder);
        }
        catch (Exception ex)
        {
            ShowError("Can't save notes in that folder: " + ex.Message);
            return;
        }

        string? fileToDeleteAfterSave = null;
        bool reload = false;
        if (!AppSettings.IsSameFolder(AppSettings.Current.ResolvedDataFolder, folder) &&
            !TryMoveNotes(folder, out fileToDeleteAfterSave, out reload))
        {
            return;
        }

        var settings = new AppSettings
        {
            DefaultColor = _selectedColour,
            ToggleHotkey = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? null : HotkeyBox.Text,
            NewNoteHotkey = string.IsNullOrWhiteSpace(NewNoteHotkeyBox.Text) ? null : NewNoteHotkeyBox.Text,
            DataFolder = AppSettings.IsSameFolder(folder, AppSettings.DefaultDataFolder) ? null : folder,
        };

        try
        {
            AppSettings.Save(settings);
        }
        catch (Exception ex)
        {
            ShowError("Couldn't save settings: " + ex.Message);
            return;
        }

        if (fileToDeleteAfterSave != null)
        {
            try { File.Delete(fileToDeleteAfterSave); } catch { }
            var oldFolder = Path.GetDirectoryName(fileToDeleteAfterSave);
            if (oldFolder != null) ImageStore.DeleteAll(oldFolder);
        }

        try
        {
            bool startup = StartupYes.IsChecked == true;
            if (StartupManager.IsEnabled != startup) StartupManager.SetEnabled(startup);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't update the startup setting:\n" + ex.Message, "Sticky Notes",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        ReloadNotes = reload && !_firstRun;
        Saved = true;
        Close();
    }

    /// <summary>
    /// Copies the current notes into <paramref name="folder"/>, or switches to the notes already there.
    /// The old file is only deleted by the caller once the new settings are saved.
    /// </summary>
    /// <returns>False if the user cancelled or the copy failed.</returns>
    private bool TryMoveNotes(string folder, out string? oldFileToDelete, out bool reload)
    {
        oldFileToDelete = null;
        reload = false;

        _saveCurrentNotes?.Invoke();
        string oldFile = NoteStore.FilePathIn(AppSettings.Current.ResolvedDataFolder);
        string newFile = NoteStore.FilePathIn(folder);

        try
        {
            if (!File.Exists(newFile))
            {
                if (File.Exists(oldFile))
                {
                    File.Copy(oldFile, newFile);
                    ImageStore.CopyAll(AppSettings.Current.ResolvedDataFolder, folder);
                    oldFileToDelete = oldFile;
                }
                return true;
            }

            if (!File.Exists(oldFile))
            {
                reload = true;
                return true;
            }

            var answer = MessageBox.Show(this,
                "That folder already contains sticky notes.\n\n" +
                "Yes: switch to the notes in that folder (your current notes stay in the old folder).\n" +
                "No: replace them with your current notes (the existing file is kept as a backup).",
                "Sticky Notes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            switch (answer)
            {
                case MessageBoxResult.Yes:
                    reload = true;
                    return true;
                case MessageBoxResult.No:
                    File.Copy(newFile, newFile + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true);
                    File.Copy(oldFile, newFile, overwrite: true);
                    ImageStore.CopyAll(AppSettings.Current.ResolvedDataFolder, folder);
                    oldFileToDelete = oldFile;
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            ShowError("Couldn't move your notes: " + ex.Message);
            return false;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string ExpandPath(string text) =>
        Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
}
