using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace StickyNotes.Tests;

[Collection(NotesCollection.Name)]
public class ImageTests
{
    private sealed class ImageResponseHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _response;

        public ImageResponseHandler(Func<CancellationToken, Task<HttpResponseMessage>> response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _response(cancellationToken);
    }

    private static void Complete(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.GetAwaiter().OnCompleted(() => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static BitmapSource Bitmap(int width = 40, int height = 30)
    {
        var pixels = Enumerable.Repeat((byte)0x80, width * height * 4).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static string PngFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "StickyNotes.Tests-" + Guid.NewGuid().ToString("N") + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Bitmap()));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static string GifFile(bool animated = true)
    {
        var path = Path.Combine(Path.GetTempPath(), "StickyNotes.Tests-" + Guid.NewGuid().ToString("N") + ".gif");
        var encoder = new GifBitmapEncoder();
        var palette = new BitmapPalette(new[] { Colors.Red, Colors.Blue });
        foreach (byte colour in animated ? new byte[] { 0, 1 } : new byte[] { 0 })
        {
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Indexed8, palette,
                new byte[] { colour, colour, colour, colour }, 2);
            var metadata = new BitmapMetadata("gif");
            metadata.SetQuery("/grctlext/Delay", (ushort)10);
            metadata.SetQuery("/grctlext/Disposal", (byte)2);
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        }
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static void Paste(NoteWindow note, IDataObject data, string format)
    {
        var editor = ManagerScope.EditorOf(note);
        editor.CaretPosition = editor.Document.ContentEnd;
        editor.RaiseEvent(new DataObjectPastingEventArgs(data, false, format));
        UiThread.Pump();
    }

    private static void AssertGifPlayback(Image image)
    {
        UiThread.Wait(100);
        var controller = Assert.IsType<WpfAnimatedGif.ImageAnimationController>(WpfAnimatedGif.ImageBehavior.GetAnimationController(image));
        Assert.Equal(2, controller.FrameCount);
        Assert.Equal(TimeSpan.FromMilliseconds(200), controller.Duration);
        Assert.False(controller.IsPaused);
        controller.Pause();

        Color Pixel()
        {
            var bitmap = new FormatConvertedBitmap((BitmapSource)image.Source, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
        }

        controller.GotoFrame(0);
        UiThread.Wait(150);
        Assert.Equal(Colors.Red, Pixel());
        controller.GotoFrame(1);
        UiThread.Wait(150);
        Assert.Same(controller, WpfAnimatedGif.ImageBehavior.GetAnimationController(image));
        Assert.Equal(Colors.Blue, Pixel());
        controller.Play();
    }

    private static InlineUIContainer ImageIn(RichTextBox editor) =>
        editor.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Hyperlink>()
            .Where(ImageEditor.IsImageLink).Single().Inlines.OfType<InlineUIContainer>().Single();

    [Fact]
    public void Browser_copied_gifs_prefer_the_original_source_over_the_static_png_preview()
    {
        UiThread.Run(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, Bitmap());
            data.SetData(DataFormats.Html,
                "Version:0.9\r\n<!--StartFragment--><img src=\"https://c.tenor.com/nAYI--YdBzIAAAAC/tenor.gif\"><!--EndFragment-->");

            Assert.Equal(new Uri("https://c.tenor.com/nAYI--YdBzIAAAAC/tenor.gif"), ImageEditor.GifSourceFrom(data));
        });
    }

    [Theory]
    [InlineData("https://example.com/image.gif?size=large&amp;loop=1", "https://example.com/image.gif?size=large&loop=1")]
    [InlineData("https://example.com/image.png", null)]
    [InlineData("file:///C:/image.gif", null)]
    [InlineData("https://user:password@example.com/image.gif", null)]
    [InlineData("http://localhost/image.gif", null)]
    public void Browser_gif_sources_are_limited_to_web_gif_images(string source, string? expected)
    {
        UiThread.Run(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, Bitmap());
            data.SetData(DataFormats.Html, $"<img src=\"{source}\">");
            Assert.Equal(expected, ImageEditor.GifSourceFrom(data)?.AbsoluteUri);
        });
    }

    [Fact]
    public void Html_without_image_clipboard_data_does_not_download_a_gif()
    {
        UiThread.Run(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.Html, "<img src='https://example.com/image.gif'>");
            data.SetText("Copied text");

            Assert.Null(ImageEditor.GifSourceFrom(data));
        });
    }

    [Fact]
    public void Browser_gif_paste_downloads_and_preserves_the_original_animation_as_one_undo_step()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("before ");
            var source = GifFile();
            try
            {
                var bytes = File.ReadAllBytes(source);
                using var client = new HttpClient(new ImageResponseHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes),
                })));
                var editor = ManagerScope.EditorOf(note);
                editor.CaretPosition = editor.Document.ContentEnd;

                Complete(note.PasteGifAsync(new Uri("https://c.tenor.com/nAYI--YdBzIAAAAC/tenor.gif"), Bitmap(), client));
                UiThread.Pump();

                var name = Assert.Single(ImageEditor.NamesIn(editor.Document));
                Assert.EndsWith(".gif", name);
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(ImageStore.Folder, name)));
                AssertGifPlayback(Assert.IsAssignableFrom<Image>(ImageIn(editor).Child));
                editor.Undo();
                UiThread.Pump();
                Assert.Empty(ImageEditor.NamesIn(editor.Document));
                Assert.Contains("before", ManagerScope.TextOf(note));
            }
            finally
            {
                File.Delete(source);
            }
        });
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("invalid")]
    [InlineData("oversized")]
    public void Browser_gif_download_failures_fall_back_to_the_clipboard_preview(string failure)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            using var client = new HttpClient(new ImageResponseHandler(_ =>
            {
                var response = new HttpResponseMessage(failure == "failed" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.ASCII.GetBytes("not a GIF")),
                };
                if (failure == "oversized") response.Content.Headers.ContentLength = 21 * 1024 * 1024;
                return Task.FromResult(response);
            }));

            Complete(note.PasteGifAsync(new Uri("https://example.com/image.gif"), Bitmap(), client));
            UiThread.Pump();

            var editor = ManagerScope.EditorOf(note);
            Assert.EndsWith(".png", Assert.Single(ImageEditor.NamesIn(editor.Document)));
            Assert.NotNull(Assert.IsType<Image>(ImageIn(editor).Child).Source);
        });
    }

    [Fact]
    public void Closing_a_note_cancels_its_pending_gif_download()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var pending = new TaskCompletionSource<HttpResponseMessage>();
            using var client = new HttpClient(new ImageResponseHandler(token => pending.Task.WaitAsync(token)));
            var task = note.PasteGifAsync(new Uri("https://example.com/image.gif"), Bitmap(), client);
            Assert.False(task.IsCompleted);

            scope.Manager.DeleteNote(note);
            Complete(task);

            Assert.True(task.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void A_pasted_image_is_saved_to_a_file_and_shown_in_the_note()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("before ");
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, Bitmap());

            Paste(note, data, DataFormats.Bitmap);

            var editor = ManagerScope.EditorOf(note);
            var name = Assert.Single(ImageEditor.NamesIn(editor.Document));
            Assert.True(File.Exists(Path.Combine(ImageStore.Folder, name)));
            var image = Assert.IsType<Image>(ImageIn(editor).Child);
            Assert.NotNull(image.Source);
        });
    }

    [Fact]
    public void A_pasted_image_file_is_copied_into_the_store()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var source = PngFile();
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.FileDrop, new[] { source, Path.ChangeExtension(source, ".txt") });

                Paste(note, data, DataFormats.FileDrop);

                var name = Assert.Single(ImageEditor.NamesIn(ManagerScope.EditorOf(note).Document));
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(ImageStore.Folder, name)));
            }
            finally
            {
                File.Delete(source);
            }
        });
    }

    [Fact]
    public void A_pasted_gif_keeps_its_original_frames_and_animates_in_the_note()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var source = GifFile();
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.FileDrop, new[] { source });
                Paste(note, data, DataFormats.FileDrop);

                var editor = ManagerScope.EditorOf(note);
                var name = Assert.Single(ImageEditor.NamesIn(editor.Document));
                Assert.EndsWith(".gif", name);
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(ImageStore.Folder, name)));
                var image = Assert.IsAssignableFrom<Image>(ImageIn(editor).Child);
                Assert.True(image.HasAnimatedProperties);
                AssertGifPlayback(image);
            }
            finally
            {
                File.Delete(source);
            }
        });
    }

    private static Hyperlink ImageLinkIn(RichTextBox editor) =>
        editor.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Hyperlink>()
            .Single(ImageEditor.IsImageLink);

    [Fact]
    public void Gif_animation_survives_resize_reload_and_undoing_image_deletion()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var source = GifFile();
            try
            {
                var name = Assert.IsType<string>(ImageStore.Import(source));
                note.InsertImages(new[] { name });
                var editor = ManagerScope.EditorOf(note);
                ImageEditor.SetWidth(editor, ImageLinkIn(editor), 1);
                UiThread.Pump();
                AssertGifPlayback(Assert.IsAssignableFrom<Image>(ImageIn(editor).Child));

                scope.Manager.SaveNow();
                scope.Manager.Reload();
                editor = ManagerScope.EditorOf(Assert.Single(scope.OpenNotes()));
                UiThread.Pump();
                Assert.Equal(1, ImageEditor.WidthOf(ImageLinkIn(editor)));
                AssertGifPlayback(Assert.IsAssignableFrom<Image>(ImageIn(editor).Child));

                ImageEditor.Remove(editor, ImageLinkIn(editor));
                UiThread.Pump();
                Assert.Empty(ImageEditor.NamesIn(editor.Document));
                editor.Undo();
                UiThread.Pump();

                var restored = Assert.IsAssignableFrom<Panel>(ImageIn(editor).Child);
                AssertGifPlayback(Assert.Single(restored.Children.OfType<Image>()));
                Assert.True(editor.CanRedo);
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(ImageStore.Folder, name)));
            }
            finally
            {
                File.Delete(source);
            }
        });
    }

    [Fact]
    public void Copying_a_gif_preserves_the_original_file_and_includes_static_fallbacks()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var source = GifFile();
            try
            {
                var name = Assert.IsType<string>(ImageStore.Import(source));
                var data = Assert.IsType<DataObject>(ImageStore.CreateClipboardData(name));
                Assert.True(data.ContainsImage());
                Assert.True(data.GetDataPresent("PNG"));
                Assert.Equal(Path.Combine(ImageStore.Folder, name), Assert.Single(data.GetFileDropList().Cast<string>()));

                Paste(note, data, DataFormats.FileDrop);

                var editor = ManagerScope.EditorOf(note);
                var pasted = Assert.Single(ImageEditor.NamesIn(editor.Document));
                Assert.EndsWith(".gif", pasted);
                Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(ImageStore.Folder, pasted)));
                AssertGifPlayback(Assert.IsAssignableFrom<Image>(ImageIn(editor).Child));
            }
            finally
            {
                File.Delete(source);
            }
        });
    }

    [Fact]
    public void A_single_frame_gif_is_shown_without_animation()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var source = GifFile(animated: false);
            try
            {
                note.InsertImages(new[] { Assert.IsType<string>(ImageStore.Import(source)) });
                UiThread.Pump();

                var image = Assert.IsAssignableFrom<Image>(ImageIn(ManagerScope.EditorOf(note)).Child);
                Assert.NotNull(image.Source);
                Assert.False(image.HasAnimatedProperties);
            }
            finally
            {
                File.Delete(source);
            }
        });
    }

    [Fact]
    public void The_viewer_plays_gifs_and_stops_the_old_animation_when_switching_images()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var source = GifFile();
            ImageViewerWindow? viewer = null;
            try
            {
                var name = Assert.IsType<string>(ImageStore.Import(source));
                viewer = ImageViewerWindow.Open(name, note);
                UiThread.Pump();
                var picture = (Image)((FrameworkElement)viewer.Content).FindName("Picture");
                AssertGifPlayback(picture);
                using (File.Open(Path.Combine(ImageStore.Folder, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

                var still = ImageStore.Save(Bitmap());
                ImageViewerWindow.Open(still, note);
                UiThread.Pump();

                Assert.False(picture.HasAnimatedProperties);
                Assert.Null(WpfAnimatedGif.ImageBehavior.GetAnimatedSource(picture));
                Assert.NotNull(picture.Source);
            }
            finally
            {
                viewer?.Close();
                File.Delete(source);
            }
        });
    }

    private static Point CentreOf(RichTextBox editor, FrameworkElement content) =>
        content.TransformToAncestor(editor).Transform(new Point(content.ActualWidth / 2, content.ActualHeight / 2));

    [Fact]
    public void Small_images_can_be_enlarged_to_the_note_width_and_keep_their_size_after_reload()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            note.InsertImages(new[] { ImageStore.Save(Bitmap(40, 30)) });
            var editor = ManagerScope.EditorOf(note);
            UiThread.Pump();
            editor.UpdateLayout();
            var content = (FrameworkElement)ImageIn(editor).Child;
            note.UpdateImageOverlay(CentreOf(editor, content));
            var overlay = Assert.IsType<ImageOverlay>(note.ImageOverlay);
            Assert.True(overlay.MaxImageWidth > 200);

            ImageEditor.SetWidth(editor, ImageLinkIn(editor), 200);
            UiThread.Pump();
            editor.UpdateLayout();
            var enlarged = Assert.IsAssignableFrom<Image>(ImageIn(editor).Child);
            Assert.InRange(enlarged.ActualWidth, 199, 201);
            Assert.InRange(enlarged.ActualHeight, 149, 151);

            note.HideImageOverlay();
            note.UpdateImageOverlay(CentreOf(editor, enlarged));
            overlay = note.ImageOverlay!;
            double maximum = overlay.MaxImageWidth;
            typeof(NoteWindow).GetMethod("CommitImageWidth", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(note, new object[] { overlay, maximum });
            UiThread.Pump();
            Assert.Equal((int)Math.Round(maximum), ImageEditor.WidthOf(ImageLinkIn(editor)));

            scope.Manager.SaveNow();
            scope.Manager.Reload();
            var restored = ManagerScope.EditorOf(Assert.Single(scope.OpenNotes()));
            UiThread.Pump();
            restored.UpdateLayout();
            Assert.True(((FrameworkElement)ImageIn(restored).Child).ActualWidth > 200);
        });
    }

    [Fact]
    public void A_resized_image_keeps_its_width_after_reloading()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            note.InsertImages(new[] { ImageStore.Save(Bitmap(200, 100)) });
            var editor = ManagerScope.EditorOf(note);

            ImageEditor.SetWidth(editor, ImageLinkIn(editor), 80);
            UiThread.Pump();
            Assert.Equal(80, ((FrameworkElement)ImageIn(editor).Child).Width);
            var data = note.CaptureData();

            scope.Manager.DeleteNote(note);
            var restored = scope.Manager.UndoDelete(data)!;
            UiThread.Pump();

            var restoredEditor = ManagerScope.EditorOf(restored);
            Assert.Equal(80, ImageEditor.WidthOf(ImageLinkIn(restoredEditor)));
            Assert.Equal(80, ((FrameworkElement)ImageIn(restoredEditor).Child).Width);
            Assert.Single(ImageStore.NamesIn(data.Content));
        });
    }

    [Fact]
    public void Resizing_an_image_can_be_undone_and_redone()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            note.InsertImages(new[] { ImageStore.Save(Bitmap(200, 100)) });
            var editor = ManagerScope.EditorOf(note);
            UiThread.Pump();

            ImageEditor.SetWidth(editor, ImageLinkIn(editor), 80);
            UiThread.Pump();
            editor.Undo();
            UiThread.Pump();

            Assert.Null(ImageEditor.WidthOf(ImageLinkIn(editor)));
            Assert.True(double.IsNaN(((FrameworkElement)ImageIn(editor).Child).Width));
            Assert.True(editor.CanRedo);

            editor.Redo();
            UiThread.Pump();
            Assert.Equal(80, ((FrameworkElement)ImageIn(editor).Child).Width);
        });
    }

    [Fact]
    public void Hovering_an_image_shows_its_toolbar_and_moving_away_hides_it()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            note.InsertImages(new[] { ImageStore.Save(Bitmap(200, 100)) });
            var editor = ManagerScope.EditorOf(note);
            UiThread.Pump();
            editor.UpdateLayout();
            var content = (FrameworkElement)ImageIn(editor).Child;

            note.UpdateImageOverlay(CentreOf(editor, content));

            var overlay = Assert.IsType<ImageOverlay>(note.ImageOverlay);
            Assert.Same(content, overlay.AdornedElement);
            Assert.True(overlay.IsEnabled); // Pictures in an editable note are disabled; the toolbar mustn't be.
            Assert.Equal(content.MaxWidth, overlay.MaxImageWidth, 1);
            Assert.True(overlay.MaxImageWidth >= 200);

            note.UpdateImageOverlay(new Point(-50, -50));
            Assert.Null(note.ImageOverlay);
        });
    }

    [Fact]
    public void The_toolbar_delete_button_removes_the_image_as_one_undo_step()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("text ");
            note.InsertImages(new[] { ImageStore.Save(Bitmap(200, 100)) });
            var editor = ManagerScope.EditorOf(note);
            UiThread.Pump();
            editor.UpdateLayout();
            note.UpdateImageOverlay(CentreOf(editor, (FrameworkElement)ImageIn(editor).Child));
            var overlay = note.ImageOverlay!;
            var root = (FrameworkElement)VisualTreeHelper.GetChild(overlay, 0);

            ((Button)root.FindName("DeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiThread.Pump();

            Assert.Empty(ImageEditor.NamesIn(editor.Document));
            Assert.Null(note.ImageOverlay);
            Assert.Contains("text", ManagerScope.TextOf(note));

            editor.Undo();
            UiThread.Pump();
            Assert.Single(ImageEditor.NamesIn(editor.Document));
        });
    }

    [Fact]
    public void The_viewer_fits_large_images_and_zooms_around_a_point()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var name = ImageStore.Save(Bitmap(4000, 3000));
            var viewer = ImageViewerWindow.Open(name, note);
            try
            {
                UiThread.Pump();
                Assert.Same(viewer, ImageViewerWindow.Current);
                Assert.Equal(name, viewer.ImageName);
                Assert.True(viewer.IsFitted);
                Assert.InRange(viewer.Scale, 0.01, 0.99);

                viewer.ZoomAt(new Point(10, 10), 1);
                Assert.Equal(1, viewer.Scale);
                Assert.False(viewer.IsFitted);

                viewer.FitToWindow();
                Assert.True(viewer.IsFitted);

                // Opening another image reuses the same viewer.
                var second = ImageStore.Save(Bitmap(20, 20));
                Assert.Same(viewer, ImageViewerWindow.Open(second, note));
                Assert.Equal(second, viewer.ImageName);
                Assert.Equal(1, viewer.Scale); // Small images aren't blown up.
            }
            finally
            {
                viewer.Close();
            }
            Assert.Null(ImageViewerWindow.Current);
        });
    }

    [Theory]
    [InlineData(null, "stickynotes-image:///img-0123456789abcdef0123456789abcdef.png")]
    [InlineData(120.4, "stickynotes-image:///img-0123456789abcdef0123456789abcdef.png?w=120")]
    public void Image_addresses_carry_the_chosen_width(double? width, string expected)
    {
        const string name = "img-0123456789abcdef0123456789abcdef.png";
        var uri = ImageEditor.UriFor(name, width);
        Assert.Equal(expected, uri.OriginalString);

        var link = new Hyperlink { NavigateUri = uri };
        Assert.Equal(name, ImageEditor.NameOf(link));
        Assert.Equal(width is null ? null : 120, ImageEditor.WidthOf(link));
    }

    [Fact]
    public void Images_survive_saving_and_reloading_a_note()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("caption");
            note.InsertImages(new[] { ImageStore.Save(Bitmap()) });
            var data = note.CaptureData();
            Assert.Contains(ImageEditor.Scheme, data.Content);

            scope.Manager.DeleteNote(note);
            var restored = scope.Manager.UndoDelete(data)!;
            UiThread.Pump();

            var editor = ManagerScope.EditorOf(restored);
            Assert.IsType<Image>(ImageIn(editor).Child);
            Assert.Contains("caption", ManagerScope.TextOf(restored));
        });
    }

    [Fact]
    public void A_missing_image_file_shows_a_placeholder()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            var name = "img-" + Guid.NewGuid().ToString("N") + ".png";
            var link = new Hyperlink(new Run(" ")) { NavigateUri = ImageEditor.UriFor(name) };
            host.SetBlocks(new Paragraph(link));

            Assert.True(ImageEditor.Hydrate(host.Editor));

            var placeholder = Assert.IsType<Border>(ImageIn(host.Editor).Child);
            Assert.Equal(name, placeholder.Tag);
            Assert.False(ImageEditor.Hydrate(host.Editor));
        });
    }

    [Fact]
    public void Deleting_an_image_can_be_undone()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("a");
            note.InsertImages(new[] { ImageStore.Save(Bitmap()) });
            UiThread.Pump();
            var editor = ManagerScope.EditorOf(note);

            editor.SelectAll();
            editor.Selection.Text = string.Empty;
            UiThread.Pump();
            Assert.Empty(ImageEditor.NamesIn(editor.Document));

            editor.Undo();
            UiThread.Pump();
            var restored = Assert.IsAssignableFrom<Panel>(ImageIn(editor).Child);
            Assert.Contains(restored.Children.OfType<Image>(), i => i.Source != null);
            Assert.True(editor.CanRedo);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Copying_an_image_between_notes_is_one_undo_step(bool animated)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var from = scope.CreateNote("x");
            string name;
            if (animated)
            {
                var source = GifFile();
                try { name = Assert.IsType<string>(ImageStore.Import(source)); }
                finally { File.Delete(source); }
            }
            else name = ImageStore.Save(Bitmap());
            from.InsertImages(new[] { name });
            var fromEditor = ManagerScope.EditorOf(from);
            var range = new TextRange(fromEditor.Document.ContentStart, fromEditor.Document.ContentEnd);
            using var ms = new MemoryStream();
            range.Save(ms, DataFormats.Xaml);
            var data = new DataObject();
            data.SetData(DataFormats.Xaml, Encoding.UTF8.GetString(ms.ToArray()));
            var to = scope.CreateNote("y");

            Paste(to, data, DataFormats.Xaml);

            var toEditor = ManagerScope.EditorOf(to);
            var image = Assert.IsAssignableFrom<Image>(ImageIn(toEditor).Child);
            if (animated) AssertGifPlayback(image);
            Assert.Equal(ImageEditor.NamesIn(fromEditor.Document), ImageEditor.NamesIn(toEditor.Document));

            toEditor.Undo();
            UiThread.Pump();
            Assert.Empty(ImageEditor.NamesIn(toEditor.Document));
        });
    }

    [Fact]
    public void Text_typed_into_an_image_link_is_moved_out_of_it()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("start");
            host.Editor.CaretPosition = host.Editor.Document.ContentEnd;
            var link = ImageEditor.Insert(host.Editor, ImageStore.Save(Bitmap()));
            link.Inlines.Add(new Run("typed"));

            Assert.True(ImageEditor.Hydrate(host.Editor));

            Assert.Single(link.Inlines);
            var paragraph = host.Editor.Document.Blocks.OfType<Paragraph>().Single();
            Assert.EndsWith("typed", new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text);
        });
    }

    [Fact]
    public void Web_links_next_to_an_image_still_work_and_leave_it_alone()
    {
        UiThread.Run(() =>
        {
            using var host = new EditorHost();
            host.SetParagraphs("https://example.com ");
            host.Editor.CaretPosition = host.Editor.Document.ContentEnd;
            ImageEditor.Insert(host.Editor, ImageStore.Save(Bitmap()));

            LinkEditor.Relink(host.Editor, includeCaret: true);

            var links = host.Editor.Document.Blocks.OfType<Paragraph>().Single().Inlines.OfType<Hyperlink>().ToList();
            Assert.Equal(2, links.Count);
            Assert.Single(links, ImageEditor.IsImageLink);
            Assert.Single(ImageEditor.NamesIn(host.Editor.Document));
        });
    }

    [Theory]
    [InlineData("stickynotes-image:///..%2F..%2Fnotes.json")]
    [InlineData("stickynotes-image:///img-abc.png")]
    [InlineData("https://example.com/img-0123456789abcdef0123456789abcdef.png")]
    public void Only_the_apps_own_image_names_are_treated_as_images(string uri)
    {
        UiThread.Run(() => Assert.Null(ImageEditor.NameOf(new Hyperlink { NavigateUri = new Uri(uri) })));
    }

    [Fact]
    public void Clean_up_deletes_only_old_unused_images_of_its_own()
    {
        var folder = Path.Combine(Path.GetTempPath(), "StickyNotes.Tests-" + Guid.NewGuid().ToString("N"));
        var images = ImageStore.FolderIn(folder);
        Directory.CreateDirectory(images);
        try
        {
            string Name() => "img-" + Guid.NewGuid().ToString("N") + ".png";
            var (saved, backedUp, live, unused, recent) = (Name(), Name(), Name(), Name(), Name());
            var now = DateTime.UtcNow;
            foreach (var name in new[] { saved, backedUp, live, unused, recent, "holiday.png" })
            {
                var path = Path.Combine(images, name);
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, name == recent ? now.AddHours(-1) : now.AddDays(-3));
            }
            File.WriteAllText(NoteStore.FilePathIn(folder), $"\"{ImageEditor.UriFor(saved)}\"");
            File.WriteAllText(NoteStore.FilePathIn(folder) + ".bak-20260101", $"\"{ImageEditor.UriFor(backedUp)}\"");

            Assert.Equal(1, ImageStore.DeleteUnused(folder, new[] { live }, now));

            var left = Directory.GetFiles(images).Select(Path.GetFileName).ToHashSet();
            Assert.DoesNotContain(unused, left);
            Assert.Superset(new[] { saved, backedUp, live, recent, "holiday.png" }.ToHashSet()!, left!);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
