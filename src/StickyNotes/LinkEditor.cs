using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace StickyNotes;

/// <summary>A web address found in a piece of text.</summary>
internal readonly record struct LinkMatch(int Index, int Length, Uri Uri);

/// <summary>
/// Turns web addresses typed or pasted into a note into clickable links (Ctrl+click opens them in the browser).
/// Links are rebuilt from the text, so editing a link's text keeps its address in step.
/// </summary>
internal static class LinkEditor
{
    private static readonly Regex UrlPattern = new(
        @"(?<![\w@/.])(?:https?://|www\.)[^\s<>""\uFFFC]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string TrailingPunctuation = ".,;:!?'\"";

    public const string ToolTipText = "Ctrl+click to open";

    /// <summary>Web addresses (http, https or www.) in <paramref name="text"/>, without trailing punctuation.</summary>
    public static IReadOnlyList<LinkMatch> Find(string text)
    {
        var links = new List<LinkMatch>();
        foreach (Match match in UrlPattern.Matches(text))
        {
            var url = TrimUrl(match.Value);
            var uri = ToUri(url);
            if (uri != null) links.Add(new LinkMatch(match.Index, url.Length, uri));
        }
        return links;
    }

    private static string TrimUrl(string url)
    {
        while (url.Length > 0)
        {
            char last = url[^1];
            if (TrailingPunctuation.Contains(last)
                || (last == ')' && url.Count(c => c == ')') > url.Count(c => c == '('))
                || (last == ']' && url.Count(c => c == ']') > url.Count(c => c == '[')))
                url = url[..^1];
            else break;
        }
        return url;
    }

    /// <summary>The address to open for some link text; only http(s) is allowed so a note can't launch programs.</summary>
    public static Uri? ToUri(string url)
    {
        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.Host.Length > 0 && (uri.Host.Contains('.') || uri.IsLoopback) ? uri : null;
    }

    /// <summary>Opens <paramref name="uri"/> in the default browser.</summary>
    public static void Open(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // No browser registered, or the user cancelled; nothing more to do.
        }
    }

    /// <summary>The link under <paramref name="point"/> (relative to the editor), if any.</summary>
    public static Hyperlink? LinkAt(RichTextBox editor, Point point)
    {
        var position = editor.GetPositionFromPoint(point, snapToText: false);
        for (var element = position?.Parent as DependencyObject; element != null && element != editor.Document;
             element = (element as TextElement)?.Parent)
        {
            if (element is Hyperlink link) return ImageEditor.IsImageLink(link) ? null : link;
        }
        return null;
    }

    /// <summary>The address a link should open, read from its current text.</summary>
    public static Uri? UriOf(Hyperlink link) =>
        ToUri(new TextRange(link.ContentStart, link.ContentEnd).Text.Trim()) ?? link.NavigateUri;

    /// <summary>
    /// Makes the links in <paramref name="document"/> match the web addresses in its text. An address the caret is
    /// touching is left alone unless <paramref name="includeCaret"/> is set, so a link appears once the user moves on
    /// (e.g. types a space) rather than while they're still typing it.
    /// </summary>
    /// <returns>True if the document was changed.</returns>
    public static bool Relink(RichTextBox editor, bool includeCaret)
    {
        bool changed = false;
        foreach (var paragraph in Paragraphs(editor.Document.Blocks).ToList())
            changed |= Relink(editor, paragraph, includeCaret);
        return changed;
    }

    private static bool Relink(RichTextBox editor, Paragraph paragraph, bool includeCaret)
    {
        var text = TextOf(paragraph);
        var wanted = Find(text).ToList();

        if (!includeCaret && editor.Selection.IsEmpty && editor.CaretPosition.Paragraph == paragraph)
        {
            int caret = OffsetOf(paragraph, editor.CaretPosition);
            wanted.RemoveAll(l => caret >= l.Index && caret <= l.Index + l.Length);
        }

        var existing = Links(paragraph.Inlines).ToList();
        var current = existing
            .Select(l => (Index: OffsetOf(paragraph, l.ContentStart), End: OffsetOf(paragraph, l.ContentEnd), l.NavigateUri))
            .ToList();
        bool same = current.Count == wanted.Count && current.Zip(wanted).All(p =>
            p.First.Index == p.Second.Index && p.First.End == p.Second.Index + p.Second.Length && p.First.NavigateUri == p.Second.Uri);
        if (same)
        {
            foreach (var link in existing) link.ToolTip ??= ToolTipText;
            return false;
        }

        bool inParagraph = editor.Selection.Start.Paragraph == paragraph && editor.Selection.End.Paragraph == paragraph;
        int selStart = inParagraph ? OffsetOf(paragraph, editor.Selection.Start) : -1;
        int selEnd = inParagraph ? OffsetOf(paragraph, editor.Selection.End) : -1;

        editor.BeginChange();
        try
        {
            foreach (var link in existing) Unwrap(link);
            foreach (var match in wanted)
            {
                var start = PositionAt(paragraph, match.Index, forward: true);
                var end = PositionAt(paragraph, match.Index + match.Length, forward: false);
                if (start == null || end == null) continue;
                try
                {
                    new Hyperlink(start, end) { NavigateUri = match.Uri, ToolTip = ToolTipText };
                }
                catch (InvalidOperationException)
                {
                    // The address crosses formatting WPF can't wrap in a link; leave it as plain text.
                }
            }
        }
        finally
        {
            editor.EndChange();
        }

        if (inParagraph)
        {
            var start = PositionAt(paragraph, selStart, forward: false);
            var end = PositionAt(paragraph, selEnd, forward: false);
            if (start != null && end != null) editor.Selection.Select(start, end);
        }
        return true;
    }

    private static void Unwrap(Hyperlink link)
    {
        var siblings = link.Parent switch
        {
            Paragraph p => p.Inlines,
            Span s => s.Inlines,
            _ => null,
        };
        if (siblings == null) return;
        foreach (var inline in link.Inlines.ToList())
        {
            link.Inlines.Remove(inline);
            siblings.InsertBefore(link, inline);
        }
        siblings.Remove(link);
    }

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return paragraph;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                    foreach (var p in Paragraphs(item.Blocks))
                        yield return p;
                    break;
                case Section section:
                    foreach (var p in Paragraphs(section.Blocks))
                        yield return p;
                    break;
            }
        }
    }

    private static IEnumerable<Hyperlink> Links(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Hyperlink link)
            {
                // Image links are managed by ImageEditor.
                if (!ImageEditor.IsImageLink(link)) yield return link;
            }
            else if (inline is Span span)
                foreach (var nested in Links(span.Inlines))
                    yield return nested;
        }
    }

    // Offsets count text characters, with embedded objects as one character, so they survive elements being
    // added or removed around the text (TextPointer symbol offsets don't).

    private static string TextOf(Paragraph paragraph)
    {
        var text = new StringBuilder();
        for (var nav = paragraph.ContentStart; nav != null && nav.CompareTo(paragraph.ContentEnd) < 0;
             nav = nav.GetNextContextPosition(LogicalDirection.Forward))
        {
            var context = nav.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text) text.Append(nav.GetTextInRun(LogicalDirection.Forward));
            else if (context == TextPointerContext.EmbeddedElement) text.Append('\uFFFC');
        }
        return text.ToString();
    }

    private static int OffsetOf(Paragraph paragraph, TextPointer position)
    {
        int offset = 0;
        for (var nav = paragraph.ContentStart; nav != null && nav.CompareTo(position) < 0;
             nav = nav.GetNextContextPosition(LogicalDirection.Forward))
        {
            var context = nav.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                int run = nav.GetTextRunLength(LogicalDirection.Forward);
                int toPosition = nav.GetOffsetToPosition(position);
                offset += Math.Min(run, toPosition);
            }
            else if (context == TextPointerContext.EmbeddedElement) offset++;
        }
        return offset;
    }

    /// <summary>
    /// The position <paramref name="chars"/> characters into the paragraph. Where that falls between two runs,
    /// <paramref name="forward"/> picks the start of the next run rather than the end of the previous one.
    /// </summary>
    private static TextPointer? PositionAt(Paragraph paragraph, int chars, bool forward)
    {
        for (var nav = paragraph.ContentStart; nav != null && nav.CompareTo(paragraph.ContentEnd) < 0;
             nav = nav.GetNextContextPosition(LogicalDirection.Forward))
        {
            var context = nav.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                int run = nav.GetTextRunLength(LogicalDirection.Forward);
                if (chars < run || (chars == run && !forward)) return nav.GetPositionAtOffset(chars);
                chars -= run;
            }
            else if (context == TextPointerContext.EmbeddedElement)
            {
                if (chars == 0 && forward) return nav;
                chars--;
            }
        }
        return chars == 0 ? paragraph.ContentEnd : null;
    }
}
