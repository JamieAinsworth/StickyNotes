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
using System.Windows.Input;
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

    private static string GifFile(bool animated = true, int width = 2, int height = 2, int frameCount = 2)
    {
        var path = Path.Combine(Path.GetTempPath(), "StickyNotes.Tests-" + Guid.NewGuid().ToString("N") + ".gif");
        var encoder = new GifBitmapEncoder();
        var palette = new BitmapPalette(new[] { Colors.Red, Colors.Blue });
        foreach (byte colour in Enumerable.Range(0, animated ? frameCount : 1).Select(index => (byte)(index % 2)))
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed8, palette,
                Enumerable.Repeat(colour, width * height).ToArray(), width);
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
        UiThread.WaitUntil(() => XamlAnimatedGif.AnimationBehavior.GetAnimator(image) != null);
        var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(image);
        Assert.Equal(2, animator.FrameCount);
        Assert.False(animator.IsPaused);
        Color CurrentPixel()
        {
            var bitmap = new FormatConvertedBitmap((BitmapSource)image.Source, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[4];
            bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixels, 4, 0);
            return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
        }
        UiThread.WaitUntil(() => CurrentPixel() == Colors.Blue);
        animator.Pause();
        animator.Rewind();
        UiThread.WaitUntil(() => CurrentPixel() == Colors.Red);
        Assert.Same(animator, XamlAnimatedGif.AnimationBehavior.GetAnimator(image));
        animator.Play();
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
    [Trait("Category", "Performance")]
    public void Restoring_gif_notes_defers_decoding_and_keeps_startup_responsive()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var source = GifFile(width: 960, height: 540, frameCount: 48);
            var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            try
            {
                var notes = new System.Collections.Generic.List<NoteWindow>();
                for (int index = 0; index < 3; index++)
                {
                    var name = ImageStore.Import(source)!;
                    var document = new FlowDocument(new Paragraph(new Hyperlink(new Run(" "))
                    {
                        NavigateUri = ImageEditor.UriFor(name),
                    }));
                    using var saved = new MemoryStream();
                    new TextRange(document.ContentStart, document.ContentEnd).Save(saved, DataFormats.Xaml);
                    var note = new NoteWindow(scope.Manager, new NoteData
                    {
                        Content = Encoding.UTF8.GetString(saved.ToArray()),
                        Left = 40 + index * 300,
                        Top = 40,
                    });
                    var image = Assert.IsType<AnimatedNoteImage>(ImageIn(ManagerScope.EditorOf(note)).Child);
                    Assert.Null(image.Source);
                    Assert.Null(XamlAnimatedGif.AnimationBehavior.GetSourceStream(image));
                    notes.Add(note);
                }

                long previous = Environment.TickCount64;
                long maximumGap = 0;
                int ticks = 0;
                heartbeat.Tick += (_, _) =>
                {
                    long current = Environment.TickCount64;
                    maximumGap = Math.Max(maximumGap, current - previous);
                    previous = current;
                    ticks++;
                };
                foreach (var note in notes) note.Show();
                previous = Environment.TickCount64;
                heartbeat.Start();
                var images = notes.Select(note => (Image)ImageIn(ManagerScope.EditorOf(note)).Child).ToList();
                UiThread.WaitUntil(() => ticks > 0 && images.All(image =>
                    XamlAnimatedGif.AnimationBehavior.GetAnimator(image)?.CurrentFrameIndex > 0), 15000);
                Assert.True(ticks > 0);
                Assert.True(maximumGap < 500, $"The UI stalled for {maximumGap}ms while restoring GIF notes.");
                foreach (var image in images)
                {
                    Assert.Equal(48, XamlAnimatedGif.AnimationBehavior.GetAnimator(image).FrameCount);
                    var name = Assert.IsType<string>(image.Tag);
                    using var unlocked = File.Open(Path.Combine(ImageStore.Folder, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }

                var closing = notes[0];
                var closingImage = images[0];
                closing.IsDeleted = true;
                closing.Close();
                UiThread.Pump();
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetSourceStream(closingImage));
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetAnimator(closingImage));
            }
            finally
            {
                heartbeat.Stop();
                File.Delete(source);
            }
        });
    }

    [Fact]
    public void Closing_a_gif_note_before_loading_finishes_does_not_restart_playback()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var source = GifFile(width: 960, height: 540, frameCount: 48);
            try
            {
                var note = scope.CreateNote();
                note.InsertImages(new[] { ImageStore.Import(source)! });
                var image = Assert.IsType<AnimatedNoteImage>(ImageIn(ManagerScope.EditorOf(note)).Child);
                scope.Manager.DeleteNote(note);
                UiThread.Pump();
                Assert.False(image.IsLoaded);
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetSourceStream(image));
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetAnimator(image));
            }
            finally
            {
                File.Delete(source);
            }
        });
    }

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
                UiThread.WaitUntil(() => XamlAnimatedGif.AnimationBehavior.GetAnimator(image) != null);
                var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(image);
                Assert.Equal(1, animator.FrameCount);
                UiThread.WaitUntil(() => animator.IsComplete);
                Assert.NotNull(image.Source);
                Assert.Equal(0, animator.CurrentFrameIndex);
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
                UiThread.WaitUntil(() => XamlAnimatedGif.AnimationBehavior.GetAnimator(picture) != null);
                AssertGifPlayback(picture);
                using (File.Open(Path.Combine(ImageStore.Folder, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

                var still = ImageStore.Save(Bitmap());
                ImageViewerWindow.Open(still, note);
                UiThread.Pump();

                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetAnimator(picture));
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetSourceStream(picture));
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

    private static void AssertViewerReopensDuringDismissal(ImageViewerWindow viewer, string name, NoteWindow note)
    {
        var root = (FrameworkElement)viewer.Content;
        var picture = (Image)root.FindName("Picture");
        var previousAnimator = XamlAnimatedGif.AnimationBehavior.GetAnimator(picture);
        Assert.False(viewer.IsTransitioning);
        Assert.Equal(1, root.Opacity);
        Assert.True(root.RenderTransform.Value.IsIdentity);

        Assert.True(viewer.DismissIfOutsideImage(new Point(0, 0)));
        Assert.True(viewer.IsTransitioning);
        if (previousAnimator != null) Assert.True(previousAnimator.IsPaused);
        Assert.Same(viewer, ImageViewerWindow.Open(name, note));
        Assert.Null(XamlAnimatedGif.AnimationBehavior.GetSourceStream(picture));
        UiThread.WaitUntil(() => !viewer.IsTransitioning);
        Assert.Same(viewer, ImageViewerWindow.Current);
        Assert.True(root.IsHitTestVisible);
        Assert.Equal(1, root.Opacity);
        Assert.True(root.RenderTransform.Value.IsIdentity);
        if (previousAnimator != null)
        {
            Assert.True(previousAnimator.IsPaused);
            UiThread.WaitUntil(() => XamlAnimatedGif.AnimationBehavior.GetAnimator(picture) is { IsPaused: false });
            Assert.NotSame(previousAnimator, XamlAnimatedGif.AnimationBehavior.GetAnimator(picture));
        }
    }

    [Fact]
    public void Gif_loading_estimates_decoded_frame_memory_without_expanding_frames()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var source = GifFile(width: 640, height: 360, frameCount: 48);
            try
            {
                var name = ImageStore.Import(source)!;
                var loading = ImageStore.LoadGifAsync(name, CancellationToken.None);
                Complete(loading);
                var data = Assert.IsType<GifData>(loading.Result);
                Assert.Equal(640L * 360 * 48, data.DecodedFrameBytes);
                Assert.True(data.Preview.IsFrozen);
            }
            finally
            {
                File.Delete(source);
            }
        });
    }

    [Fact]
    public void Gif_frame_caching_respects_per_image_and_shared_memory_budgets()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var source = GifFile();
            var playbacks = new System.Collections.Generic.List<GifPlayback>();
            long original = GifPlayback.CachedBytes;
            try
            {
                var data = new GifData(Bitmap(), File.ReadAllBytes(source), GifPlayback.PerImageCacheLimit);
                int slots = (int)((GifPlayback.TotalCacheLimit - original) / GifPlayback.PerImageCacheLimit);
                for (int index = 0; index < slots; index++)
                {
                    var image = new Image();
                    var playback = new GifPlayback(image);
                    playbacks.Add(playback);
                    playback.Start(data);
                    Assert.True(XamlAnimatedGif.AnimationBehavior.GetCacheFramesInMemory(image));
                }
                var fallbackImage = new Image();
                var fallback = new GifPlayback(fallbackImage);
                playbacks.Add(fallback);
                fallback.Start(data);
                Assert.False(XamlAnimatedGif.AnimationBehavior.GetCacheFramesInMemory(fallbackImage));
                Assert.NotNull(XamlAnimatedGif.AnimationBehavior.GetSourceStream(fallbackImage));
                Assert.True(GifPlayback.CachedBytes <= GifPlayback.TotalCacheLimit);

                if (slots > 0)
                {
                    playbacks[0].Stop();
                    fallback.Start(data);
                    Assert.True(XamlAnimatedGif.AnimationBehavior.GetCacheFramesInMemory(fallbackImage));
                }
                fallback.Start(data with { DecodedFrameBytes = GifPlayback.PerImageCacheLimit + 1 });
                Assert.False(XamlAnimatedGif.AnimationBehavior.GetCacheFramesInMemory(fallbackImage));
                using var host = new EditorHost();
                host.SetBlocks(new Paragraph(new InlineUIContainer(fallbackImage)));
                AssertGifPlayback(fallbackImage);
                fallback.Stop();
                fallback.Stop();
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetSourceStream(fallbackImage));
            }
            finally
            {
                foreach (var playback in playbacks) playback.Dispose();
                File.Delete(source);
            }
            Assert.Equal(original, GifPlayback.CachedBytes);
        });
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void The_viewer_keeps_the_dispatcher_responsive_while_loading_a_multiframe_gif()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var source = GifFile(width: 960, height: 540, frameCount: 48);
            ImageViewerWindow? viewer = null;
            var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            try
            {
                var name = ImageStore.Import(source)!;
                Assert.NotNull(ImageStore.Load(name));
                long previous = Environment.TickCount64;
                long maximumGap = 0;
                int ticks = 0;
                heartbeat.Tick += (_, _) =>
                {
                    long current = Environment.TickCount64;
                    maximumGap = Math.Max(maximumGap, current - previous);
                    previous = current;
                    ticks++;
                };
                heartbeat.Start();
                viewer = ImageViewerWindow.Open(name, note);
                var picture = (Image)((FrameworkElement)viewer.Content).FindName("Picture");
                UiThread.WaitUntil(() => XamlAnimatedGif.AnimationBehavior.GetAnimator(picture)?.CurrentFrameIndex > 0, 15000);
                Assert.True(ticks > 0);
                Assert.True(maximumGap < 500, $"The UI dispatcher stalled for {maximumGap}ms during GIF loading.");
                Assert.True(XamlAnimatedGif.AnimationBehavior.GetCacheFramesInMemory(picture));
                Assert.Equal(48, XamlAnimatedGif.AnimationBehavior.GetAnimator(picture).FrameCount);
                Assert.IsType<WriteableBitmap>(picture.Source);

                var still = ImageStore.Save(Bitmap());
                ImageViewerWindow.Open(name, note);
                ImageViewerWindow.Open(still, note);
                UiThread.WaitUntil(() => !viewer.IsTransitioning);
                UiThread.Pump();
                Assert.Equal(still, viewer.ImageName);
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetSourceStream(picture));
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetAnimator(picture));
                ImageViewerWindow.Open(name, note);
                viewer.Close();
                UiThread.Pump();
                Assert.Null(ImageViewerWindow.Current);
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetSourceStream(picture));
            }
            finally
            {
                heartbeat.Stop();
                if (viewer?.IsVisible == true) viewer.Close();
                File.Delete(source);
            }
        });
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(640, 360)]
    public void The_viewer_defers_gif_initialization_until_opening_finishes_and_pauses_while_closing(int width, int height)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var source = GifFile(width: width, height: height);
            ImageViewerWindow? viewer = null;
            try
            {
                var name = ImageStore.Import(source)!;
                viewer = ImageViewerWindow.Open(name, note);
                var picture = (Image)((FrameworkElement)viewer.Content).FindName("Picture");
                Assert.True(viewer.IsTransitioning);
                Assert.NotNull(picture.Source);
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetSourceStream(picture));
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetAnimator(picture));
                var root = (FrameworkElement)viewer.Content;
                Assert.IsType<BitmapCache>(root.CacheMode);
                viewer.ZoomAt(new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2), 0.75);
                UiThread.WaitUntil(() => XamlAnimatedGif.AnimationBehavior.GetAnimator(picture) is { IsPaused: false });
                Assert.False(viewer.IsTransitioning);
                Assert.Null(root.CacheMode);
                Assert.Equal(0.75, viewer.Scale, 6);
                var controller = XamlAnimatedGif.AnimationBehavior.GetAnimator(picture)!;
                Assert.Equal(2, controller.FrameCount);

                AssertViewerReopensDuringDismissal(viewer, name, note);
                viewer.DismissIfOutsideImage(new Point(0, 0));
                UiThread.WaitUntil(() => ImageViewerWindow.Current == null);
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetSourceStream(picture));
            }
            finally
            {
                if (viewer?.IsVisible == true) viewer.Close();
                File.Delete(source);
            }
        });
    }

    [Theory]
    [InlineData(false, MouseButton.Left)]
    [InlineData(true, MouseButton.Left)]
    [InlineData(false, MouseButton.Right)]
    [InlineData(true, MouseButton.Right)]
    public void Clicking_an_image_selects_it_without_opening_the_viewer(bool animated, MouseButton button)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("before ");
            var source = animated ? GifFile() : PngFile();
            try
            {
                var name = ImageStore.Import(source)!;
                note.InsertImages(new[] { name });
                var editor = ManagerScope.EditorOf(note);
                UiThread.Pump();
                editor.UpdateLayout();
                var link = ImageLinkIn(editor);

                Assert.True(note.HandleImageClick(CentreOf(editor, (FrameworkElement)ImageIn(editor).Child), button, 1));

                Assert.Null(ImageViewerWindow.Current);
                Assert.True(editor.Selection.Start.CompareTo(link.ContentStart) <= 0);
                Assert.True(editor.Selection.End.CompareTo(link.ContentEnd) >= 0);
                Assert.True(ApplicationCommands.Copy.CanExecute(null, editor));
                Assert.True(ApplicationCommands.Cut.CanExecute(null, editor));
                var copied = new TextRange(editor.Selection.Start, editor.Selection.End);
                using var stream = new MemoryStream();
                copied.Save(stream, DataFormats.Xaml);
                Assert.Contains(name, Encoding.UTF8.GetString(stream.ToArray()));
                Assert.DoesNotContain("before", Encoding.UTF8.GetString(stream.ToArray()));

                ApplicationCommands.Copy.Execute(null, editor);
                Assert.True(System.Windows.Clipboard.ContainsImage());
                if (animated)
                    Assert.Contains(Path.Combine(ImageStore.Folder, name), System.Windows.Clipboard.GetFileDropList().Cast<string>());

                ApplicationCommands.Cut.Execute(null, editor);
                UiThread.Pump();
                Assert.Empty(ImageEditor.NamesIn(editor.Document));
                Assert.Contains("before", ManagerScope.TextOf(note));
                editor.Undo();
                UiThread.Pump();
                Assert.Equal(name, Assert.Single(ImageEditor.NamesIn(editor.Document)));
            }
            finally
            {
                File.Delete(source);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Double_clicking_an_image_opens_the_viewer(bool animated)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var source = animated ? GifFile() : PngFile();
            try
            {
                var name = ImageStore.Import(source)!;
                note.InsertImages(new[] { name });
                var editor = ManagerScope.EditorOf(note);
                UiThread.Pump();
                editor.UpdateLayout();
                var point = CentreOf(editor, (FrameworkElement)ImageIn(editor).Child);
                Assert.True(note.HandleImageClick(point, MouseButton.Left, 1));
                Assert.Null(ImageViewerWindow.Current);

                Assert.True(note.HandleImageClick(point, MouseButton.Left, 2));

                Assert.Equal(name, Assert.IsType<ImageViewerWindow>(ImageViewerWindow.Current).ImageName);
            }
            finally
            {
                ImageViewerWindow.Current?.Close();
                File.Delete(source);
            }
        });
    }

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
    public void A_deleted_note_restores_its_image_caption_and_resized_width()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("caption");
            var name = ImageStore.Save(Bitmap(200, 100));
            note.InsertImages(new[] { name });
            var editor = ManagerScope.EditorOf(note);

            ImageEditor.SetWidth(editor, ImageLinkIn(editor), 80);
            UiThread.Pump();
            Assert.Equal(80, ((FrameworkElement)ImageIn(editor).Child).Width);
            var data = note.CaptureData();
            Assert.Contains(ImageEditor.Scheme, data.Content);
            Assert.Equal(name, Assert.Single(ImageStore.NamesIn(data.Content)));

            scope.Manager.DeleteNote(note);
            var restored = scope.Manager.UndoDelete(data)!;
            UiThread.Pump();

            var restoredEditor = ManagerScope.EditorOf(restored);
            Assert.Equal(80, ImageEditor.WidthOf(ImageLinkIn(restoredEditor)));
            var image = Assert.IsType<Image>(ImageIn(restoredEditor).Child);
            Assert.NotNull(image.Source);
            Assert.Equal(80, image.Width);
            Assert.Equal(name, Assert.Single(ImageEditor.NamesIn(restoredEditor.Document)));
            Assert.Contains("caption", ManagerScope.TextOf(restored));
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

    [Theory]
    [InlineData(4000, 3000)]
    [InlineData(3000, 4000)]
    [InlineData(4800, 1200)]
    public void The_viewer_fits_large_images_and_zooms_around_a_point(int width, int height)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var name = ImageStore.Save(Bitmap(width, height));
            var viewer = ImageViewerWindow.Open(name, note);
            try
            {
                UiThread.Pump();
                Assert.Same(viewer, ImageViewerWindow.Current);
                Assert.Equal(name, viewer.ImageName);
                Assert.True(viewer.IsFitted);
                var root = (FrameworkElement)viewer.Content;
                var surface = (FrameworkElement)root.FindName("Surface");
                var picture = (Image)root.FindName("Picture");
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(picture.Source);
                double expectedScale = Math.Min(1, Math.Min(surface.ActualWidth * 0.8 / bitmap.Width,
                    surface.ActualHeight * 0.8 / bitmap.Height));
                Assert.Equal(expectedScale, viewer.Scale, 6);
                var bounds = picture.RenderTransform.TransformBounds(new Rect(picture.RenderSize));
                Assert.InRange(bounds.Width, 0, surface.ActualWidth * 0.8 + 1);
                Assert.InRange(bounds.Height, 0, surface.ActualHeight * 0.8 + 1);
                Assert.InRange(bounds.Left + bounds.Width / 2, surface.ActualWidth / 2 - 1, surface.ActualWidth / 2 + 1);
                Assert.InRange(bounds.Top + bounds.Height / 2, surface.ActualHeight / 2 - 1, surface.ActualHeight / 2 + 1);

                viewer.ZoomAt(new Point(10, 10), 1);
                Assert.Equal(1, viewer.Scale);
                Assert.False(viewer.IsFitted);

                viewer.FitToWindow();
                Assert.True(viewer.IsFitted);
                Assert.Equal(expectedScale, viewer.Scale, 6);

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
    [InlineData(false)]
    [InlineData(true)]
    public void The_viewer_overlays_the_notes_display_and_outside_click_returns_to_the_note(bool animated)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote("before ");
            var source = animated ? GifFile() : PngFile();
            ImageViewerWindow? viewer = null;
            try
            {
                var name = ImageStore.Import(source)!;
                note.InsertImages(new[] { name });
                var noteHandle = new System.Windows.Interop.WindowInteropHelper(note).Handle;
                var display = System.Windows.Forms.Screen.FromHandle(noteHandle).Bounds;
                viewer = ImageViewerWindow.Open(name, note);
                UiThread.Pump();

                Assert.Equal(WindowStyle.None, viewer.WindowStyle);
                Assert.Equal(WindowState.Normal, viewer.WindowState);
                Assert.Equal(ResizeMode.NoResize, viewer.ResizeMode);
                Assert.True(viewer.AllowsTransparency);
                Assert.True(viewer.Topmost);
                Assert.False(viewer.ShowInTaskbar);
                Assert.Equal(1, Assert.IsType<SolidColorBrush>(viewer.Background).Color.A);
                Assert.True(NativeMethods.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(viewer).Handle, out var actual));
                Assert.Equal(PixelRect.From(display), new PixelRect(actual.Left, actual.Top, actual.Right, actual.Bottom));

                var root = (FrameworkElement)viewer.Content;
                var picture = (Image)root.FindName("Picture");
                var surface = (FrameworkElement)root.FindName("Surface");
                UiThread.WaitUntil(() => !viewer.IsTransitioning);
                var centre = picture.PointToScreen(new Point(picture.ActualWidth / 2, picture.ActualHeight / 2));
                Assert.InRange(centre.X, display.Left + display.Width / 2.0 - 1, display.Left + display.Width / 2.0 + 1);
                Assert.InRange(centre.Y, display.Top + display.Height / 2.0 - 1, display.Top + display.Height / 2.0 + 1);
                if (animated) AssertGifPlayback(picture);

                Assert.False(viewer.DismissIfOutsideImage(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2)));
                Assert.Same(viewer, ImageViewerWindow.Current);
                Assert.True(viewer.DismissIfOutsideImage(new Point(0, 0)));
                UiThread.WaitUntil(() => ImageViewerWindow.Current == null);

                Assert.Null(ImageViewerWindow.Current);
                Assert.True(note.IsActive);
                Assert.Equal(name, Assert.Single(ImageEditor.NamesIn(ManagerScope.EditorOf(note).Document)));
                Assert.Contains("before", ManagerScope.TextOf(note));
                Assert.Null(XamlAnimatedGif.AnimationBehavior.GetSourceStream(picture));
            }
            finally
            {
                if (viewer?.IsVisible == true) viewer.Close();
                File.Delete(source);
            }
        });
    }

    [Fact]
    public void The_viewer_animates_opening_and_closing_and_can_reopen_during_dismissal()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var name = ImageStore.Save(Bitmap(600, 400));
            var viewer = ImageViewerWindow.Open(name, note);
            try
            {
                var root = (FrameworkElement)viewer.Content;
                Assert.True(viewer.IsTransitioning);
                UiThread.WaitUntil(() => !viewer.IsTransitioning);
                AssertViewerReopensDuringDismissal(viewer, name, note);

                var close = (Button)root.FindName("CloseButton");
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(viewer.IsTransitioning);
                UiThread.WaitUntil(() => ImageViewerWindow.Current == null);
                Assert.False(viewer.IsVisible);
                Assert.True(note.IsActive);
            }
            finally
            {
                if (viewer.IsVisible) viewer.Close();
            }
        });
    }

    [Fact]
    public void The_viewer_expands_from_the_inline_image_and_returns_to_its_current_position()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var name = ImageStore.Save(Bitmap(600, 400));
            note.InsertImages(new[] { name });
            UiThread.Pump();
            note.UpdateLayout();
            var source = Assert.IsType<Rect>(note.ImageBoundsOnScreen(name));
            var viewer = ImageViewerWindow.Open(name, note);
            try
            {
                var root = (FrameworkElement)viewer.Content;
                var picture = (Image)root.FindName("Picture");
                var imageBounds = picture.RenderTransform.TransformBounds(new Rect(picture.RenderSize));
                var origin = new Rect(viewer.PointFromScreen(source.TopLeft), viewer.PointFromScreen(source.BottomRight));
                if (SystemParameters.ClientAreaAnimation)
                {
                    var initial = root.RenderTransform.TransformBounds(imageBounds);
                    Assert.InRange(initial.Left, origin.Left - 1, origin.Left + 1);
                    Assert.InRange(initial.Top, origin.Top - 1, origin.Top + 1);
                    Assert.InRange(initial.Width, origin.Width - 1, origin.Width + 1);
                    Assert.InRange(initial.Height, origin.Height - 1, origin.Height + 1);
                }
                UiThread.WaitUntil(() => !viewer.IsTransitioning);
                Assert.True(root.RenderTransform.Value.IsIdentity);

                viewer.ZoomAt(new Point(viewer.ActualWidth / 2 + 40, viewer.ActualHeight / 2), 0.8);
                note.Left -= 40;
                UiThread.Pump();
                source = Assert.IsType<Rect>(note.ImageBoundsOnScreen(name));
                origin = new Rect(viewer.PointFromScreen(source.TopLeft), viewer.PointFromScreen(source.BottomRight));
                imageBounds = picture.RenderTransform.TransformBounds(new Rect(picture.RenderSize));
                Assert.True(viewer.DismissIfOutsideImage(new Point(0, 0)));
                var transforms = Assert.IsType<TransformGroup>(root.RenderTransform);
                var scale = Assert.IsType<ScaleTransform>(transforms.Children[0]);
                var translate = Assert.IsType<TranslateTransform>(transforms.Children[1]);
                var target = new Matrix(
                    origin.Width / imageBounds.Width, 0,
                    0, origin.Height / imageBounds.Height,
                    origin.Left - imageBounds.Left * origin.Width / imageBounds.Width,
                    origin.Top - imageBounds.Top * origin.Height / imageBounds.Height);
                var destination = new MatrixTransform(target).TransformBounds(imageBounds);
                Assert.InRange(destination.Left, origin.Left - 1, origin.Left + 1);
                Assert.InRange(destination.Top, origin.Top - 1, origin.Top + 1);
                Assert.InRange(destination.Width, origin.Width - 1, origin.Width + 1);
                Assert.InRange(destination.Height, origin.Height - 1, origin.Height + 1);
                UiThread.WaitUntil(() => !viewer.IsTransitioning);
                Assert.InRange(scale.ScaleX, target.M11 - 0.001, target.M11 + 0.001);
                Assert.InRange(scale.ScaleY, target.M22 - 0.001, target.M22 + 0.001);
                Assert.InRange(translate.X, target.OffsetX - 1, target.OffsetX + 1);
                Assert.InRange(translate.Y, target.OffsetY - 1, target.OffsetY + 1);
                UiThread.WaitUntil(() => ImageViewerWindow.Current == null);
                Assert.Equal(name, Assert.Single(ImageEditor.NamesIn(ManagerScope.EditorOf(note).Document)));
            }
            finally
            {
                if (viewer.IsVisible) viewer.Close();
            }
        });
    }

    [Fact]
    public void The_viewer_zoom_buttons_change_zoom_by_five_percentage_points()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var viewer = ImageViewerWindow.Open(ImageStore.Save(Bitmap(600, 400)), scope.CreateNote());
            try
            {
                var root = (FrameworkElement)viewer.Content;
                var zoomIn = (Button)root.FindName("ZoomInButton");
                var zoomOut = (Button)root.FindName("ZoomOutButton");
                var zoomText = (TextBlock)root.FindName("ZoomText");
                foreach (double start in new[] { 0.62, 1.0, 0.02, 31.98 })
                {
                    viewer.ZoomAt(new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2), start);
                    zoomIn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    double expected = Math.Min(32, start + 0.05);
                    Assert.Equal(expected, viewer.Scale, 6);
                    Assert.Equal($"{expected * 100:0}%", zoomText.Text);
                    zoomOut.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(Math.Max(0.02, expected - 0.05), viewer.Scale, 6);
                }
                viewer.ZoomAt(new Point(), 0.03);
                zoomOut.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0.02, viewer.Scale);
            }
            finally
            {
                viewer.Close();
            }
        });
    }

    [Fact]
    public void The_viewer_zoom_percentage_drags_in_both_directions_and_releases_capture()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var viewer = ImageViewerWindow.Open(ImageStore.Save(Bitmap(600, 400)), scope.CreateNote());
            try
            {
                UiThread.WaitUntil(() => !viewer.IsTransitioning);
                var zoomText = (TextBlock)((FrameworkElement)viewer.Content).FindName("ZoomText");
                Assert.Equal(Cursors.SizeWE, zoomText.Cursor);
                viewer.ZoomAt(new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2), 0.62);
                viewer.BeginZoomDrag(200);
                Assert.True(zoomText.IsMouseCaptured);
                viewer.DragZoomTo(240);
                Assert.Equal(0.72, viewer.Scale, 6);
                viewer.DragZoomTo(180);
                Assert.Equal(0.57, viewer.Scale, 6);
                viewer.DragZoomTo(100000);
                Assert.Equal(32, viewer.Scale);
                viewer.DragZoomTo(200);
                Assert.Equal(0.62, viewer.Scale, 6);
                viewer.DragZoomTo(-100000);
                Assert.Equal(0.02, viewer.Scale);
                viewer.EndZoomDrag();
                Assert.False(zoomText.IsMouseCaptured);
                viewer.DragZoomTo(240);
                Assert.Equal(0.02, viewer.Scale);

                viewer.BeginZoomDrag(200);
                zoomText.ReleaseMouseCapture();
                viewer.DragZoomTo(240);
                Assert.Equal(0.02, viewer.Scale);
                viewer.BeginZoomDrag(200);
                viewer.DismissIfOutsideImage(new Point(0, 0));
                Assert.False(zoomText.IsMouseCaptured);
                UiThread.WaitUntil(() => ImageViewerWindow.Current == null);
            }
            finally
            {
                if (viewer.IsVisible) viewer.Close();
            }
        });
    }

    [Fact]
    public void The_viewer_border_and_toolbar_follow_the_image_and_scale_down_with_it()
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var viewer = ImageViewerWindow.Open(ImageStore.Save(Bitmap(600, 400)), note);
            try
            {
                UiThread.Pump();
                var root = (FrameworkElement)viewer.Content;
                var picture = (Image)root.FindName("Picture");
                var toolbar = (Border)root.FindName("ViewerToolbar");
                var border = (Border)root.FindName("ImageBorder");
                var shadowLayer = (Canvas)root.FindName("ShadowLayer");
                var shadowShape = (Border)root.FindName("ImageShadow");
                var shadow = Assert.IsType<System.Windows.Media.Effects.DropShadowEffect>(shadowShape.Effect);
                Assert.Equal(Colors.Black, shadow.Color);
                Assert.True(shadow.BlurRadius > 0);
                Assert.True(shadow.ShadowDepth > 0);
                Assert.InRange(shadow.Opacity, 0.1, 1);
                Assert.Null(border.Background);
                Assert.Null(picture.Effect);
                Assert.False(shadowLayer.IsHitTestVisible);
                double previousWidth = double.PositiveInfinity;
                foreach (double scale in new[] { 1.0, 0.4, 0.2, 1.0 })
                {
                    viewer.ZoomAt(new Point(viewer.ActualWidth / 2 + 40, viewer.ActualHeight / 2), scale);
                    UiThread.Pump();
                    viewer.UpdateLayout();
                    var bounds = picture.RenderTransform.TransformBounds(new Rect(picture.RenderSize));
                    var toolbarBounds = toolbar.TransformToAncestor(root).TransformBounds(new Rect(toolbar.RenderSize));
                    Assert.InRange(toolbarBounds.Left + toolbarBounds.Width / 2, bounds.Left + bounds.Width / 2 - 1,
                        bounds.Left + bounds.Width / 2 + 1);
                    Assert.InRange(toolbarBounds.Top, bounds.Bottom + 9, bounds.Bottom + 11);
                    Assert.InRange(border.Width, bounds.Width + 1, bounds.Width + 3);
                    Assert.InRange(border.Height, bounds.Height + 1, bounds.Height + 3);
                    Assert.InRange(Canvas.GetLeft(border), bounds.Left - 2, bounds.Left);
                    Assert.InRange(Canvas.GetTop(border), bounds.Top - 2, bounds.Top);
                    if (scale < 1) Assert.True(toolbarBounds.Width <= previousWidth);
                    previousWidth = toolbarBounds.Width;
                    Assert.True(toolbarBounds.Left >= 0 && toolbarBounds.Right <= root.ActualWidth);
                    Assert.True(toolbarBounds.Top >= 0 && toolbarBounds.Bottom <= root.ActualHeight);

                    var renderedShadow = new RenderTargetBitmap((int)Math.Ceiling(shadowLayer.ActualWidth),
                        (int)Math.Ceiling(shadowLayer.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    renderedShadow.Render(shadowLayer);
                    byte AlphaAt(double horizontal, double vertical)
                    {
                        var pixel = new byte[4];
                        renderedShadow.CopyPixels(new Int32Rect((int)horizontal, (int)vertical, 1, 1), pixel, 4, 0);
                        return pixel[3];
                    }
                    Assert.True(AlphaAt(bounds.Right + 4, bounds.Top + bounds.Height / 2) >= 16,
                        "The shadow must visibly darken the background outside the image.");
                    Assert.Equal(0, AlphaAt(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
                    Assert.Equal(0, AlphaAt(bounds.Left + 2, bounds.Top + bounds.Height / 2));
                    Assert.Equal(0, AlphaAt(bounds.Right - 2, bounds.Top + bounds.Height / 2));
                    Assert.Equal(0, AlphaAt(bounds.Left + bounds.Width / 2, bounds.Top + 2));
                    Assert.Equal(0, AlphaAt(bounds.Left + bounds.Width / 2, bounds.Bottom - 2));
                }
            }
            finally
            {
                viewer.Close();
            }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_unreadable_gifs_show_visible_selectable_placeholders(bool corrupt)
    {
        UiThread.Run(() =>
        {
            using var scope = new ManagerScope();
            var note = scope.CreateNote();
            var name = "img-" + Guid.NewGuid().ToString("N") + ".gif";
            if (corrupt)
            {
                Directory.CreateDirectory(ImageStore.Folder);
                File.WriteAllText(Path.Combine(ImageStore.Folder, name), "not a GIF");
            }
            note.InsertImages(new[] { name });
            var editor = ManagerScope.EditorOf(note);
            var image = Assert.IsType<AnimatedNoteImage>(ImageIn(editor).Child);
            UiThread.WaitUntil(() => image.Source is DrawingImage);
            editor.UpdateLayout();
            Assert.Equal("Image not found", image.ToolTip);
            Assert.True(image.ActualWidth > 0);
            Assert.True(image.ActualHeight > 0);
            Assert.Equal(name, ImageEditor.HitAt(editor, CentreOf(editor, image))?.Name);
            Assert.False(ImageEditor.Hydrate(editor));
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
