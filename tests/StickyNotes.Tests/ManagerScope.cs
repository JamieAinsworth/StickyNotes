using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace StickyNotes.Tests;

/// <summary>Creates a <see cref="NoteManager"/> and closes every note it opened when disposed. Use on the UI thread.</summary>
internal sealed class ManagerScope : IDisposable
{
    public NoteManager Manager { get; }

    public ManagerScope()
    {
        // Start every test from an empty notes file, whatever an earlier test saved.
        var file = NoteStore.FilePathIn(AppSettings.Current.ResolvedDataFolder);
        if (File.Exists(file)) File.Delete(file);

        Manager = new NoteManager();
    }

    /// <summary>Notes currently open (deleted ones are closed and not counted).</summary>
    public List<NoteWindow> OpenNotes() =>
        Application.Current.Windows.OfType<NoteWindow>().Where(w => !w.IsDeleted).ToList();

    public NoteWindow CreateNote(string text = "")
    {
        var note = Manager.CreateNote();
        if (text.Length > 0) SetText(note, text);
        return note;
    }

    public static RichTextBox EditorOf(NoteWindow note) => (RichTextBox)note.FindName("Editor");

    public static void SetText(NoteWindow note, string text) =>
        new TextRange(EditorOf(note).Document.ContentStart, EditorOf(note).Document.ContentEnd).Text = text;

    public static string TextOf(NoteWindow note) =>
        new TextRange(EditorOf(note).Document.ContentStart, EditorOf(note).Document.ContentEnd).Text.Trim();

    public void Dispose()
    {
        Manager.Shutdown();
        foreach (var note in OpenNotes())
        {
            note.IsDeleted = true;
            note.Close();
        }
        UiThread.Pump();
    }
}
