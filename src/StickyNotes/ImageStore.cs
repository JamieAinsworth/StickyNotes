using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace StickyNotes;

/// <summary>
/// Image files for notes, kept in an "images" folder next to notes.json. Notes refer to them by file name only,
/// so the notes folder can be moved or synced as a whole.
/// </summary>
internal static class ImageStore
{
    public const string FolderName = "images";

    /// <summary>Images wider than this are scaled down for display; Ctrl+click still opens the original.</summary>
    private const int MaxDisplayPixels = 1600;

    /// <summary>
    /// An unused image is kept at least this long before it's deleted, so a notes folder shared between PCs (e.g. in
    /// OneDrive) doesn't lose an image another PC has just added while its notes.json is still syncing.
    /// </summary>
    public static readonly TimeSpan UnusedGracePeriod = TimeSpan.FromDays(1);

    private static readonly Regex OwnFileName = new(@"^img-[0-9a-f]{32}\.(png|jpg|gif|bmp)$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> ImportExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = ".png",
        [".jpg"] = ".jpg",
        [".jpeg"] = ".jpg",
        [".gif"] = ".gif",
        [".bmp"] = ".bmp",
    };

    private static readonly Dictionary<string, BitmapSource> Cache = new();
    private static readonly HttpClient Http = new();
    private const int MaxGifBytes = 20 * 1024 * 1024;

    public static string Folder => FolderIn(AppSettings.Current.ResolvedDataFolder);

    public static string FolderIn(string dataFolder) => Path.Combine(dataFolder, FolderName);

    /// <summary>True for file names this app creates; anything else in the folder is never read, opened or deleted.</summary>
    public static bool IsOwnFileName(string? name) => name != null && OwnFileName.IsMatch(name);

    public static bool CanImport(string path) => ImportExtensions.ContainsKey(Path.GetExtension(path));

    /// <summary>Saves a pasted image and returns its file name.</summary>
    public static string Save(BitmapSource bitmap)
    {
        var name = NewName(".png");
        Directory.CreateDirectory(Folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(Folder, name))) encoder.Save(stream);
        return name;
    }

    /// <summary>Copies a dropped or pasted image file into the store. Returns null if it isn't a readable image.</summary>
    public static string? Import(string path)
    {
        if (!ImportExtensions.TryGetValue(Path.GetExtension(path), out var extension)) return null;
        try
        {
            using (var stream = File.OpenRead(path))
                BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            var name = NewName(extension);
            Directory.CreateDirectory(Folder);
            var target = Path.Combine(Folder, name);
            File.Copy(path, target);
            // A copy keeps the original's date; the clean-up's grace period should count from now.
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or FileFormatException)
        {
            return null;
        }
    }

    internal static async Task<string?> DownloadGifAsync(Uri uri, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.IsLoopback) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await (client ?? Http).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxGifBytes) return null;
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (bytes.Length + count > MaxGifBytes) return null;
                bytes.Write(buffer, 0, count);
            }
            bytes.Position = 0;
            if (BitmapDecoder.Create(bytes, BitmapCreateOptions.None, BitmapCacheOption.OnLoad) is not GifBitmapDecoder) return null;

            var name = NewName(".gif");
            Directory.CreateDirectory(Folder);
            await File.WriteAllBytesAsync(Path.Combine(Folder, name), bytes.ToArray(), timeout.Token);
            return name;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
                                       or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The image for display, or null if the file is missing or unreadable.</summary>
    public static BitmapSource? Load(string name)
    {
        if (!IsOwnFileName(name)) return null;
        if (Cache.TryGetValue(name, out var cached)) return cached;

        var path = Path.Combine(Folder, name);
        try
        {
            var bytes = File.ReadAllBytes(path);
            int width;
            using (var probe = new MemoryStream(bytes))
                width = BitmapDecoder.Create(probe, BitmapCreateOptions.None, BitmapCacheOption.None).Frames[0].PixelWidth;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            if (width > MaxDisplayPixels) image.DecodePixelWidth = MaxDisplayPixels;
            image.EndInit();
            image.Freeze();
            Cache[name] = image;
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The image at full resolution (not cached), for the viewer and copying, or null if it can't be read.</summary>
    public static BitmapSource? LoadFull(string name)
    {
        if (!IsOwnFileName(name)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(File.ReadAllBytes(Path.Combine(Folder, name)));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Puts the image on the clipboard. Returns false if it couldn't be read or the clipboard was busy.</summary>
    public static bool CopyToClipboard(string name)
    {
        if (CreateClipboardData(name) is not { } data) return false;
        try
        {
            System.Windows.Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    internal static System.Windows.DataObject? CreateClipboardData(string name)
    {
        if (LoadFull(name) is not { } bitmap) return null;
        var data = new System.Windows.DataObject();
        data.SetImage(bitmap);
        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(png);
        png.Position = 0;
        data.SetData("PNG", png);
        if (name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { Path.Combine(Folder, name) });
        return data;
    }

    /// <summary>Image file names referred to in some saved note content.</summary>
    public static IEnumerable<string> NamesIn(string? content) =>
        string.IsNullOrEmpty(content)
            ? Enumerable.Empty<string>()
            : ImageEditor.UriPattern.Matches(content).Select(m => m.Groups[1].Value).Where(IsOwnFileName);

    /// <summary>
    /// Deletes this app's image files in <paramref name="dataFolder"/> that no note uses any more. An image counts as
    /// used if it's in <paramref name="inUse"/> or mentioned in notes.json or any backup of it, and nothing is
    /// deleted within <see cref="UnusedGracePeriod"/> of being written.
    /// </summary>
    /// <returns>The number of files deleted.</returns>
    public static int DeleteUnused(string dataFolder, IEnumerable<string> inUse, DateTime? nowUtc = null)
    {
        var folder = FolderIn(dataFolder);
        if (!Directory.Exists(folder)) return 0;

        var used = new HashSet<string>(inUse, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(dataFolder, Path.GetFileName(NoteStore.FilePathIn(dataFolder)) + "*"))
                used.UnionWith(NamesIn(File.ReadAllText(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If the notes can't be read, it isn't safe to decide what's unused.
            return 0;
        }

        var cutoff = (nowUtc ?? DateTime.UtcNow) - UnusedGracePeriod;
        int deleted = 0;
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(path);
            if (!IsOwnFileName(name) || used.Contains(name)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(path) > cutoff) continue;
                File.Delete(path);
                Cache.Remove(name);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or read-only; try again next time.
            }
        }
        return deleted;
    }

    /// <summary>Copies this app's image files to another notes folder, skipping ones already there.</summary>
    public static void CopyAll(string fromDataFolder, string toDataFolder)
    {
        var from = FolderIn(fromDataFolder);
        if (!Directory.Exists(from)) return;
        var to = FolderIn(toDataFolder);
        Directory.CreateDirectory(to);
        foreach (var path in Directory.EnumerateFiles(from))
        {
            var name = Path.GetFileName(path);
            var target = Path.Combine(to, name);
            if (IsOwnFileName(name) && !File.Exists(target)) File.Copy(path, target);
        }
    }

    /// <summary>Deletes this app's image files from a notes folder the notes have moved out of.</summary>
    public static void DeleteAll(string dataFolder)
    {
        var folder = FolderIn(dataFolder);
        if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            if (!IsOwnFileName(Path.GetFileName(path))) continue;
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string NewName(string extension) => "img-" + Guid.NewGuid().ToString("N") + extension;
}
