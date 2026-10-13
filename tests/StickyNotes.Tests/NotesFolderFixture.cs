using System;
using System.IO;
using Xunit;

namespace StickyNotes.Tests;

/// <summary>
/// Points the app at a throwaway data folder for the tests that create notes, so saving never touches the
/// user's real notes. Tests that share it run one at a time (see <see cref="NotesCollection"/>).
/// </summary>
public sealed class NotesFolderFixture : IDisposable
{
    private readonly AppSettings _original;

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "StickyNotes.Tests-" + Guid.NewGuid().ToString("N"));

    public NotesFolderFixture()
    {
        Directory.CreateDirectory(Folder);
        _original = AppSettings.Current;
        AppSettings.Current = new AppSettings { DataFolder = Folder };
    }

    public void Dispose()
    {
        AppSettings.Current = _original;
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
    }
}

[CollectionDefinition(Name)]
public sealed class NotesCollection : ICollectionFixture<NotesFolderFixture>
{
    public const string Name = "Notes";
}
