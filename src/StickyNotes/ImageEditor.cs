using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StickyNotes;

internal sealed record ImageHit(Hyperlink Link, string Name, FrameworkElement Content);

/// <summary>
/// Images in a note. WPF drops pictures when it saves rich text, but keeps a link's address, so each image sits in a
/// <see cref="Hyperlink"/> whose address names its file in <see cref="ImageStore"/>. When a note is loaded or content
/// is pasted, <see cref="Hydrate"/> puts the picture back inside each of those links.
/// </summary>
internal static class ImageEditor
{
    public const string Scheme = "stickynotes-image";

    public const string ToolTipText = "Double-click to preview";

    public static readonly Regex UriPattern = new(Scheme + @":///([\w.-]+)", RegexOptions.Compiled);

    private const double ListIndent = 22;

    /// <summary>The address for an image, with the width it was resized to, if any (saved as <c>?w=</c>).</summary>
    public static Uri UriFor(string name, double? width = null) =>
        width is { } w && w >= 1
            ? new($"{Scheme}:///{name}?w={(int)Math.Round(w)}")
            : new($"{Scheme}:///{name}");

    /// <summary>The width an image was resized to, or null if it fits the note.</summary>
    public static double? WidthOf(Hyperlink link)
    {
        var query = link.NavigateUri is { IsAbsoluteUri: true } uri ? uri.Query : null;
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var part in query.TrimStart('?').Split('&'))
        {
            if (part.StartsWith("w=", StringComparison.Ordinal)
                && int.TryParse(part.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int width)
                && width > 0)
                return width;
        }
        return null;
    }

    /// <summary>The image file a link stands for, or null if it's an ordinary link.</summary>
    public static string? NameOf(Hyperlink link)
    {
        var uri = link.NavigateUri;
        if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Scheme) return null;
        var name = uri.AbsolutePath.TrimStart('/');
        return ImageStore.IsOwnFileName(name) ? name : null;
    }

    public static bool IsImageLink(Hyperlink link) => NameOf(link) != null;

    public static void AddClipboardFormats(RichTextBox editor, IDataObject data)
    {
        if (editor.Selection.IsEmpty) return;
        foreach (var (link, name) in ImageLinks(editor.Document))
        {
            var range = new TextRange(link.ElementStart, link.ElementEnd);
            if (editor.Selection.Start.CompareTo(range.Start) != 0
                || editor.Selection.End.CompareTo(range.End) != 0) continue;
            if (ImageStore.CreateClipboardData(name) is not { } imageData) return;
            foreach (var format in imageData.GetFormats(autoConvert: false))
                data.SetData(format, imageData.GetData(format));
            return;
        }
    }

    /// <summary>Inserts an image at the caret, replacing any selected text, and puts the caret after it.</summary>
    public static Hyperlink Insert(RichTextBox editor, string name)
    {
        editor.BeginChange();
        try
        {
            if (!editor.Selection.IsEmpty) editor.Selection.Text = string.Empty;
            var position = editor.Selection.Start.GetInsertionPosition(LogicalDirection.Forward);

            // Links can't contain links, so an image dropped into a web link goes just after it.
            for (var element = position.Parent as TextElement; element != null; element = element.Parent as TextElement)
            {
                if (element is Hyperlink outer) position = outer.ElementEnd;
            }

            var link = new Hyperlink(new InlineUIContainer(CreateContent(name)), position);
            Style(link, name);
            FitToWidth(editor);
            editor.Selection.Select(link.ElementEnd, link.ElementEnd);
            return link;
        }
        finally
        {
            editor.EndChange();
        }
    }

    /// <summary>
    /// Puts the picture back in every image link that lost it (after loading or pasting), moves text typed into an
    /// image link out of it, and removes links whose image was deleted. With <paramref name="repairOnly"/> it only
    /// refills the empty boxes WPF puts back when deleting an image is undone, which isn't itself an undoable change.
    /// </summary>
    /// <returns>True if the document was changed.</returns>
    public static bool Hydrate(RichTextBox editor, bool repairOnly = false)
    {
        bool changed = false;
        foreach (var (link, name) in ImageLinks(editor.Document).ToList())
        {
            var inlines = link.Inlines.ToList();
            foreach (var container in inlines.OfType<InlineUIContainer>())
            {
                if (container.Child is Panel { Tag: null, Children.Count: 0 } restored)
                {
                    var content = CreateContent(name);
                    restored.Tag = name;
                    restored.ToolTip = content.ToolTip;
                    ToolTipService.SetShowOnDisabled(restored, true);
                    restored.Children.Add(content);
                    FitToWidth(editor);
                    break;
                }
            }
            if (repairOnly) continue;

            var ours = inlines.OfType<InlineUIContainer>().FirstOrDefault(c => IsContentFor(c, name));
            if (ours != null && inlines.Count == 1) continue;

            if (!changed) editor.BeginChange();
            changed = true;

            if (ours != null)
            {
                MoveOut(link, inlines.TakeWhile(i => i != ours), before: true);
                MoveOut(link, inlines.SkipWhile(i => i != ours).Skip(1), before: false);
            }
            else if (inlines.OfType<InlineUIContainer>().Any() || new TextRange(link.ContentStart, link.ContentEnd).Text == " ")
            {
                // A saved or copied image (WPF writes it as a space) or one pasted from WPF's own image format.
                link.Inlines.Clear();
                link.Inlines.Add(new InlineUIContainer(CreateContent(name)));
                Style(link, name);
            }
            else
            {
                // The picture was deleted; keep any text that ended up in the link and drop the link itself.
                MoveOut(link, inlines, before: true);
                Siblings(link)?.Remove(link);
            }
        }

        if (changed)
        {
            FitToWidth(editor);
            editor.EndChange();
        }
        return changed;
    }

    /// <summary>Limits images to the width of the note so they're never cut off, and applies any width they were resized to.</summary>
    public static void FitToWidth(RichTextBox editor)
    {
        double width = editor.ViewportWidth > 0 ? editor.ViewportWidth : editor.ActualWidth;
        width -= editor.Padding.Left + editor.Padding.Right + 4;
        foreach (var (link, _) in ImageLinks(editor.Document))
        {
            if (link.Inlines.FirstInline is not InlineUIContainer { Child: FrameworkElement content }) continue;
            int depth = 0;
            for (var e = link.Parent as TextElement; e != null; e = e.Parent as TextElement)
                if (e is List) depth++;
            content.MaxWidth = Math.Max(24, width - depth * ListIndent);
            if (WidthOf(link) is { } chosen) content.Width = chosen;
            else content.ClearValue(FrameworkElement.WidthProperty);
        }
    }

    /// <summary>The image under <paramref name="point"/> (relative to the editor), if any.</summary>
    public static ImageHit? HitAt(RichTextBox editor, Point point)
    {
        foreach (var (link, name) in ImageLinks(editor.Document))
        {
            if (link.Inlines.FirstInline is not InlineUIContainer { Child: FrameworkElement content } || !content.IsVisible)
                continue;
            try
            {
                var bounds = content.TransformToAncestor(editor).TransformBounds(new Rect(content.RenderSize));
                if (bounds.Contains(point)) return new ImageHit(link, name, content);
            }
            catch (InvalidOperationException)
            {
                // Not laid out yet.
            }
        }
        return null;
    }

    /// <summary>The image file under <paramref name="point"/> (relative to the editor), if any.</summary>
    public static string? ImageAt(RichTextBox editor, Point point) => HitAt(editor, point)?.Name;

    public static Rect? ScreenBoundsOf(RichTextBox editor, string name)
    {
        foreach (var (link, imageName) in ImageLinks(editor.Document))
        {
            if (imageName != name || link.Inlines.FirstInline is not InlineUIContainer { Child: FrameworkElement content }
                || !content.IsVisible || content.ActualWidth <= 0 || content.ActualHeight <= 0) continue;
            var bounds = new Rect(content.PointToScreen(new Point()),
                content.PointToScreen(new Point(content.ActualWidth, content.ActualHeight)));
            var viewport = new Rect(editor.PointToScreen(new Point()),
                editor.PointToScreen(new Point(editor.ActualWidth, editor.ActualHeight)));
            bounds.Intersect(viewport);
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0) return bounds;
        }
        return null;
    }

    /// <summary>
    /// Sets the width an image is shown at (null to fit the note) as one undoable change. WPF's undo doesn't record
    /// changes to a link's address, so the image's link is swapped for a new one instead.
    /// </summary>
    public static Hyperlink? SetWidth(RichTextBox editor, Hyperlink link, double? width)
    {
        var name = NameOf(link);
        var siblings = Siblings(link);
        if (name == null || siblings == null) return null;
        var uri = UriFor(name, width);
        if (uri == link.NavigateUri) return link;

        var replacement = new Hyperlink(new InlineUIContainer(CreateContent(name))) { NavigateUri = uri };
        Style(replacement, name);
        editor.BeginChange();
        try
        {
            siblings.InsertAfter(link, replacement);
            siblings.Remove(link);
        }
        finally
        {
            editor.EndChange();
        }
        FitToWidth(editor);
        return replacement;
    }

    /// <summary>Takes an image out of the note as one undoable change.</summary>
    public static void Remove(RichTextBox editor, Hyperlink link)
    {
        var siblings = Siblings(link);
        if (siblings == null) return;
        editor.BeginChange();
        try
        {
            siblings.Remove(link);
        }
        finally
        {
            editor.EndChange();
        }
    }

    /// <summary>The picture shown by an image's content (an image, or the box WPF restores on undo).</summary>
    public static BitmapSource? BitmapOf(FrameworkElement content) => content switch
    {
        Image { Source: BitmapSource bitmap } => bitmap,
        Panel panel => panel.Children.OfType<Image>().Select(i => i.Source).OfType<BitmapSource>().FirstOrDefault(),
        _ => null,
    };

    /// <summary>The image files used in a document, in order.</summary>
    public static IEnumerable<string> NamesIn(FlowDocument document) => ImageLinks(document).Select(l => l.Name);

    /// <summary>The original web GIF referenced by a browser image clipboard, if any.</summary>
    public static Uri? GifSourceFrom(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent("PNG") && !data.GetDataPresent(DataFormats.Bitmap)) return null;
            if (data.GetData(DataFormats.Html) is not string html || html.Length > 1024 * 1024) return null;

            var document = new HtmlAgilityPack.HtmlDocument();
            document.LoadHtml(html);
            var images = document.DocumentNode.SelectNodes("//img[@src]");
            if (images?.Count != 1) return null;
            var source = System.Net.WebUtility.HtmlDecode(images[0].GetAttributeValue("src", string.Empty));
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
                && uri.Scheme is "https" or "http"
                && uri.UserInfo.Length == 0
                && !uri.IsLoopback
                && uri.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return uri;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
        return null;
    }

    public static BitmapSource? ImageFrom(IDataObject data)
    {
        try
        {
            // Prefer PNG: it keeps transparency, which the plain bitmap format mangles.
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is System.IO.Stream png)
                return BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
                return new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotSupportedException
                                       or System.IO.FileFormatException or ArgumentException)
        {
            // Clipboard owner went away or the image is unreadable.
        }
        return null;
    }

    /// <summary>Image files in dropped or copied file data.</summary>
    public static IReadOnlyList<string> ImageFilesIn(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
                return files.Where(ImageStore.CanImport).ToList();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
        return Array.Empty<string>();
    }

    private static FrameworkElement CreateContent(string name)
    {
        FrameworkElement content;
        if (name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            content = new AnimatedNoteImage { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
        }
        else if (ImageStore.Load(name) is { } bitmap)
        {
            content = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
            RenderOptions.SetBitmapScalingMode(content, BitmapScalingMode.HighQuality);
        }
        else
        {
            content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0, 0, 0)),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock { Text = "Image not found", FontStyle = FontStyles.Italic, Foreground = Brushes.DimGray },
            };
        }
        content.Tag = name;
        content.ToolTip = ToolTipText;
        ToolTipService.SetShowOnDisabled(content, true);
        return content;
    }

    internal static ImageSource MissingImageSource()
    {
        var text = new FormattedText("Image not found", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal),
            SystemFonts.MessageFontSize, Brushes.DimGray, 1);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x18, 0, 0, 0)), null,
                new Rect(0, 0, text.Width + 12, text.Height + 4));
            context.DrawText(text, new Point(6, 2));
        }
        drawing.Freeze();
        return new DrawingImage(drawing);
    }

    private static void Style(Hyperlink link, string name)
    {
        var uri = UriFor(name, WidthOf(link));
        if (uri != link.NavigateUri) link.NavigateUri = uri;
        link.TextDecorations = new TextDecorationCollection();
        link.ToolTip = ToolTipText;
    }

    private static bool IsContentFor(InlineUIContainer container, string name) =>
        container.Child is FrameworkElement { Tag: string tag } && tag == name;

    private static void MoveOut(Hyperlink link, IEnumerable<Inline> inlines, bool before)
    {
        var siblings = Siblings(link);
        if (siblings == null) return;
        var list = inlines.ToList();
        if (!before) list.Reverse();
        foreach (var inline in list)
        {
            link.Inlines.Remove(inline);
            if (before) siblings.InsertBefore(link, inline);
            else siblings.InsertAfter(link, inline);
        }
    }

    private static InlineCollection? Siblings(Inline inline) => inline.Parent switch
    {
        Paragraph p => p.Inlines,
        Span s => s.Inlines,
        _ => null,
    };

    private static IEnumerable<(Hyperlink Link, string Name)> ImageLinks(FlowDocument document) =>
        Inlines(document.Blocks)
            .OfType<Hyperlink>()
            .Select(l => (Link: l, Name: NameOf(l)))
            .Where(p => p.Name != null)
            .Select(p => (p.Link, p.Name!));

    private static IEnumerable<Inline> Inlines(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    foreach (var inline in Inlines(paragraph.Inlines)) yield return inline;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                    foreach (var inline in Inlines(item.Blocks))
                        yield return inline;
                    break;
                case Section section:
                    foreach (var inline in Inlines(section.Blocks)) yield return inline;
                    break;
            }
        }
    }

    private static IEnumerable<Inline> Inlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
                foreach (var nested in Inlines(span.Inlines))
                    yield return nested;
        }
    }
}

public sealed class AnimatedNoteImage : Image
{
    private CancellationTokenSource? _loading;
    private readonly GifPlayback _playback;
    private BitmapSource? _preview;

    public AnimatedNoteImage()
    {
        _playback = new GifPlayback(this);
        Loaded += (_, _) => _ = LoadAsync();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) =>
        {
            var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(this);
            if (!IsVisible) animator?.Pause();
            else if (animator is { IsComplete: false }) animator.Play();
        };
        XamlAnimatedGif.AnimationBehavior.AddLoadedHandler(this, (_, _) =>
        {
            var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(this);
            if (animator == null) return;
            var animated = Source;
            Source = _preview ?? animated;
            XamlAnimatedGif.AnimationBehavior.SetAutoStart(this, IsVisible);
            EventHandler? firstFrame = null;
            firstFrame = (_, _) =>
            {
                animator.CurrentFrameChanged -= firstFrame;
                if (IsLoaded && ReferenceEquals(animator, XamlAnimatedGif.AnimationBehavior.GetAnimator(this)))
                    Source = animated;
            };
            animator.CurrentFrameChanged += firstFrame;
        });
        XamlAnimatedGif.AnimationBehavior.AddErrorHandler(this, (_, _) =>
        {
            Stop();
            Source = _preview ?? ImageEditor.MissingImageSource();
            ToolTip = "Image not found";
        });
    }

    private async Task LoadAsync()
    {
        if (_loading != null || _playback.IsActive || Tag is not string name) return;
        var loading = new CancellationTokenSource();
        _loading = loading;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            loading.Token.ThrowIfCancellationRequested();
            var data = await ImageStore.LoadGifAsync(name, loading.Token);
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (loading.IsCancellationRequested || !IsLoaded || !ReferenceEquals(_loading, loading)) return;
            if (data == null)
            {
                Source = ImageEditor.MissingImageSource();
                ToolTip = "Image not found";
                return;
            }
            _preview = data.Preview;
            Source = _preview;
            _playback.Start(data);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_loading, loading)) _loading = null;
            loading.Dispose();
        }
    }

    private void Stop()
    {
        _loading?.Cancel();
        _loading = null;
        _playback.Stop();
    }

    protected override bool ShouldSerializeProperty(DependencyProperty dependencyProperty) =>
        dependencyProperty != SourceProperty
        && dependencyProperty.OwnerType != typeof(XamlAnimatedGif.AnimationBehavior)
        && base.ShouldSerializeProperty(dependencyProperty);
}
