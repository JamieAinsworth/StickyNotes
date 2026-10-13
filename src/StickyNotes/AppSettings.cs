using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StickyNotes;

/// <summary>
/// User preferences. Always stored in %APPDATA%\StickyNotes\settings.json so the app can find
/// the notes even when they're kept somewhere else.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StickyNotes");

    public static string SettingsPath { get; } = Path.Combine(DefaultDataFolder, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public string DefaultColor { get; set; } = NoteColors.Default.Name;

    /// <summary>Folder holding notes.json; null or empty means <see cref="DefaultDataFolder"/>.</summary>
    public string? DataFolder { get; set; }

    /// <summary>Global hotkey that shows/hides all notes, e.g. "Ctrl+Alt+N"; null or empty means none.</summary>
    public string? ToggleHotkey { get; set; }

    /// <summary>Global hotkey to create a new note, e.g. "Ctrl+Alt+M"; null or empty means none.</summary>
    public string? NewNoteHotkey { get; set; }

    [JsonIgnore]
    public string ResolvedDataFolder =>
        string.IsNullOrWhiteSpace(DataFolder) ? DefaultDataFolder : DataFolder;

    [JsonIgnore]
    public bool UsesCustomFolder => !string.IsNullOrWhiteSpace(DataFolder) && !IsSameFolder(DataFolder, DefaultDataFolder);

    /// <summary>Loads settings; returns false if none exist yet (first run).</summary>
    public static bool Load()
    {
        MigrateLegacyData(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RhyhoStickyNotes"),
            DefaultDataFolder);

        try
        {
            if (!File.Exists(SettingsPath)) return false;
            Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
            return true;
        }
        catch (Exception)
        {
            // Unreadable settings: fall back to defaults and let the user run setup again.
            Current = new AppSettings();
            return false;
        }
    }

    internal static void MigrateLegacyData(string legacyFolder, string destinationFolder)
    {
        string destinationSettings = Path.Combine(destinationFolder, "settings.json");
        if (File.Exists(destinationSettings) || !Directory.Exists(legacyFolder)) return;

        string legacySettings = Path.Combine(legacyFolder, "settings.json");
        AppSettings? settings = null;
        if (File.Exists(legacySettings))
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(legacySettings), JsonOptions)
                ?? throw new InvalidDataException("The previous settings file is empty.");
            if (!string.IsNullOrWhiteSpace(settings.DataFolder) && IsSameFolder(settings.DataFolder, legacyFolder))
                settings.DataFolder = null;
        }

        string oldNotes = Path.Combine(legacyFolder, "notes.json");
        string newNotes = Path.Combine(destinationFolder, "notes.json");
        if (File.Exists(oldNotes) && File.Exists(newNotes) &&
            File.ReadAllText(oldNotes) != File.ReadAllText(newNotes))
        {
            throw new IOException("Both the old and new data folders contain different notes. " +
                "No files were overwritten. Please back up and resolve these files before restarting:\n" +
                oldNotes + "\n" + newNotes);
        }

        Directory.CreateDirectory(destinationFolder);
        if (File.Exists(oldNotes) && !File.Exists(newNotes))
            File.Copy(oldNotes, newNotes);

        if (settings != null)
        {
            string temporarySettings = destinationSettings + ".tmp";
            File.WriteAllText(temporarySettings, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporarySettings, destinationSettings);
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DefaultDataFolder);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, SettingsPath, overwrite: true);
        Current = settings;
    }

    public static bool IsSameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
}
