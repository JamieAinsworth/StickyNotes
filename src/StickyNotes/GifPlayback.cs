using System;
using System.IO;
using System.Windows.Controls;

namespace StickyNotes;

internal sealed class GifPlayback : IDisposable
{
    internal const long PerImageCacheLimit = 32L * 1024 * 1024;
    internal const long TotalCacheLimit = 128L * 1024 * 1024;

    private static readonly object CacheLock = new();
    private static long _cachedBytes;
    private readonly Image _image;
    private MemoryStream? _stream;
    private long _reservedBytes;

    internal GifPlayback(Image image) => _image = image;

    internal bool IsActive => _stream != null;

    internal static long CachedBytes
    {
        get { lock (CacheLock) return _cachedBytes; }
    }

    internal void Start(GifData data)
    {
        Stop();
        lock (CacheLock)
        {
            if (data.DecodedFrameBytes > 0 && data.DecodedFrameBytes <= PerImageCacheLimit
                && data.DecodedFrameBytes <= TotalCacheLimit - _cachedBytes)
            {
                _reservedBytes = data.DecodedFrameBytes;
                _cachedBytes += _reservedBytes;
            }
        }
        try
        {
            _stream = new MemoryStream(data.Bytes, writable: false);
            XamlAnimatedGif.AnimationBehavior.SetCacheFramesInMemory(_image, _reservedBytes > 0);
            XamlAnimatedGif.AnimationBehavior.SetSourceStream(_image, _stream);
        }
        catch
        {
            Stop();
            throw;
        }
    }

    internal void Stop()
    {
        var stream = _stream;
        _stream = null;
        try
        {
            XamlAnimatedGif.AnimationBehavior.GetAnimator(_image)?.Dispose();
            if (XamlAnimatedGif.AnimationBehavior.GetSourceStream(_image) != null)
                XamlAnimatedGif.AnimationBehavior.SetSourceStream(_image, null);
        }
        finally
        {
            stream?.Dispose();
            lock (CacheLock)
            {
                _cachedBytes -= _reservedBytes;
                _reservedBytes = 0;
            }
        }
    }

    public void Dispose() => Stop();
}