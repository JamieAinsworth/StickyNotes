using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace StickyNotes.Tests;

/// <summary>An off-screen rich text box that behaves like a note's editor, plus helpers to inspect it.</summary>
internal sealed class EditorHost : IDisposable
{
    public const string Empty = "\u2610";
    public const string Done = "\u2611";

    private readonly Window _window;

    public RichTextBox Editor { get; }

    public static readonly Color TextColour = Color.FromRgb(0x20, 0x20, 0x20);

    /// <summary>Uses a plain "Segoe UI" editor font, like a note loaded from disk.</summary>
    public EditorHost()
    {
        Editor = new RichTextBox
        {
            Width = 300,
            Height = 220,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = new SolidColorBrush(TextColour),
        };
        _window = new Window
        {
            Content = Editor,
            Left = -5000,
            Top = 0,
            Width = 320,
            Height = 240,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        _window.Show();
        UiThread.Pump();
    }

    /// <summary>Replaces the document with one plain paragraph per line.</summary>
    public void SetParagraphs(params string[] lines)
    {
        Editor.Document.Blocks.Clear();
        foreach (var line in lines) Editor.Document.Blocks.Add(new Paragraph(new Run(line)));
        UiThread.Pump();
    }

    public void SetBlocks(params Block[] blocks)
    {
        Editor.Document.Blocks.Clear();
        foreach (var block in blocks) Editor.Document.Blocks.Add(block);
        UiThread.Pump();
    }

    /// <summary>Selects the text of the top-level paragraphs <paramref name="from"/> to <paramref name="to"/>.</summary>
    public void SelectParagraphs(int from, int to)
    {
        var paragraphs = Editor.Document.Blocks.OfType<Paragraph>().ToList();
        Editor.Selection.Select(paragraphs[from].ContentStart, paragraphs[to].ContentEnd);
    }

    public void Select(Paragraph paragraph) => Editor.Selection.Select(paragraph.ContentStart, paragraph.ContentEnd);

    /// <summary>The paragraphs inside the first list, in order.</summary>
    public List<Paragraph> ListItems() =>
        Editor.Document.Blocks.OfType<List>().First().ListItems
            .Select(i => i.Blocks.OfType<Paragraph>().First()).ToList();

    public Paragraph FirstItem() => ListItems()[0];

    public static TextPointer Start(Paragraph paragraph) =>
        paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);

    /// <summary>Moves forward by whole characters (run boundaries don't count).</summary>
    public static TextPointer After(TextPointer pointer, int characters)
    {
        for (int i = 0; i < characters; i++) pointer = pointer.GetNextInsertionPosition(LogicalDirection.Forward)!;
        return pointer;
    }

    /// <summary>Puts the caret <paramref name="characters"/> into the paragraph's text.</summary>
    public void Caret(Paragraph paragraph, int characters) => Editor.CaretPosition = After(Start(paragraph), characters);

    /// <summary>Presses Enter at the caret and leaves the caret at the start of the new paragraph.</summary>
    public Paragraph PressEnter()
    {
        Editor.CaretPosition.InsertParagraphBreak();
        Editor.CaretPosition = Editor.CaretPosition.GetNextInsertionPosition(LogicalDirection.Forward) ?? Editor.CaretPosition;
        return Editor.CaretPosition.Paragraph!;
    }

    public TextRange TextOf(Paragraph paragraph, int skip, int length)
    {
        var start = After(Start(paragraph), skip);
        return new TextRange(start, After(start, length));
    }

    public static bool Has(TextRange range, TextDecorationLocation location) =>
        range.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection decorations &&
        decorations.Any(d => d.Location == location);

    /// <summary>The document as text, e.g. <c>LIST(None){ P[☐ a] P[☐ b] } P[c]</c>.</summary>
    public string Dump()
    {
        var builder = new StringBuilder();

        void Walk(BlockCollection blocks)
        {
            foreach (var block in blocks)
            {
                if (block is Paragraph paragraph)
                {
                    builder.Append("P[").Append(ChecklistEditor.ParagraphText(paragraph)).Append("] ");
                }
                else if (block is List list)
                {
                    builder.Append("LIST(").Append(list.MarkerStyle).Append("){ ");
                    foreach (var item in list.ListItems) Walk(item.Blocks);
                    builder.Append("} ");
                }
            }
        }

        Walk(Editor.Document.Blocks);
        return builder.ToString().Trim();
    }

    public void Dispose() => _window.Close();
}
