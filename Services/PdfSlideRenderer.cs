using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFtoImage;
using SkiaSharp;

namespace Scribble.Services;

/// <summary>
/// Renders crisp, annotation-free PDF slide bitmaps using PDFium (PDFtoImage)
/// and maintains a small bounded cache around the active slide.
/// </summary>
public sealed class PdfSlideRenderer : IDisposable
{
    private readonly byte[] _pdfBytes;
    private readonly ConcurrentDictionary<int, BitmapSource> _cache = new();
    private readonly object _renderLock = new();
    private const int TargetRenderWidthPx = 2880;
    private const int MaxCachedPages = 7;

    public int PageCount { get; }

    public PdfSlideRenderer(byte[] pdfBytes)
    {
        _pdfBytes = pdfBytes;
        using var ms = new MemoryStream(_pdfBytes, writable: false);
        PageCount = Conversion.GetPageCount(ms);
    }

    public BitmapSource GetSlideBitmap(int pageIndex)
    {
        if (_cache.TryGetValue(pageIndex, out var cached))
        {
            return cached;
        }

        BitmapSource rendered = RenderPage(pageIndex);
        _cache[pageIndex] = rendered;
        EvictDistantPages(pageIndex);
        return rendered;
    }

    public void PrefetchAdjacent(int currentPageIndex)
    {
        int[] candidates = [currentPageIndex + 1, currentPageIndex - 1];
        foreach (int idx in candidates)
        {
            if (idx >= 0 && idx < PageCount && !_cache.ContainsKey(idx))
            {
                int pageToRender = idx;
                Task.Run(() =>
                {
                    if (!_cache.ContainsKey(pageToRender))
                    {
                        var bmp = RenderPage(pageToRender);
                        _cache.TryAdd(pageToRender, bmp);
                    }
                });
            }
        }
    }

    private BitmapSource RenderPage(int pageIndex)
    {
        lock (_renderLock)
        {
            if (_cache.TryGetValue(pageIndex, out var cached))
            {
                return cached;
            }

            using var ms = new MemoryStream(_pdfBytes, writable: false);
            var options = new PDFtoImage.RenderOptions(
                Width: TargetRenderWidthPx,
                WithAnnotations: false,
                WithFormFill: false,
                WithAspectRatio: true,
                BackgroundColor: SKColors.White);

            using SKBitmap skBitmap = Conversion.ToImage(ms, page: pageIndex, options: options);
            return ConvertToFrozenWpfBitmap(skBitmap);
        }
    }

    private static BitmapSource ConvertToFrozenWpfBitmap(SKBitmap skBitmap)
    {
        // Ensure BGRA8888 premultiplied format matching WPF PixelFormats.Pbgra32
        SKBitmap source = skBitmap;
        SKBitmap? converted = null;

        if (skBitmap.ColorType != SKColorType.Bgra8888)
        {
            converted = new SKBitmap(skBitmap.Width, skBitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(converted);
            canvas.DrawBitmap(skBitmap, 0, 0);
            source = converted;
        }

        try
        {
            int stride = source.RowBytes;
            int bufferSize = stride * source.Height;
            var wpfBitmap = BitmapSource.Create(
                source.Width,
                source.Height,
                96,
                96,
                PixelFormats.Pbgra32,
                null,
                source.GetPixels(),
                bufferSize,
                stride);

            wpfBitmap.Freeze();
            return wpfBitmap;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private void EvictDistantPages(int anchorIndex)
    {
        if (_cache.Count <= MaxCachedPages)
        {
            return;
        }

        var toRemove = _cache.Keys
            .OrderByDescending(k => Math.Abs(k - anchorIndex))
            .Take(_cache.Count - MaxCachedPages)
            .ToList();

        foreach (int key in toRemove)
        {
            _cache.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _cache.Clear();
    }
}
