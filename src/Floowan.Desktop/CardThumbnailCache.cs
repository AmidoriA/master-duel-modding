using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Floowan.Core.Assets;
using Floowan.Core.Game;
using Floowan.Core.Models;

namespace Floowan.Desktop;

/// <summary>
/// Loads and caches small card-art previews for thumbnail list views.
/// Missing/unreadable bundles resolve to <see cref="Placeholder"/> without throwing.
/// </summary>
internal sealed class CardThumbnailCache : IDisposable
{
    private readonly CardArtBundleService _bundles = new();
    private readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(3);
    private readonly ImageSource _placeholder;

    public CardThumbnailCache()
    {
        _placeholder = CreatePlaceholder();
    }

    public ImageSource Placeholder => _placeholder;

    public static string CacheKey(CardRecord card) => $"{card.Id}\u001f{card.Bundle}";

    public bool TryGet(CardRecord card, out ImageSource image)
    {
        if (_cache.TryGetValue(CacheKey(card), out var hit))
        {
            image = hit;
            return true;
        }

        image = _placeholder;
        return false;
    }

    public void Invalidate(CardRecord card) => _cache.TryRemove(CacheKey(card), out _);

    public async Task<ImageSource> GetOrLoadAsync(string? gamePath, CardRecord card, CancellationToken cancellationToken = default)
    {
        var key = CacheKey(card);
        if (_cache.TryGetValue(key, out var hit))
            return hit;

        if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(card.Bundle))
            return _placeholder;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out hit))
                return hit;

            try
            {
                var bundlePath = BundlePathResolver.ResolveExistingBundlePath(gamePath, card.Bundle);
                var tempPath = Path.Combine(Path.GetTempPath(), $"floowan-thumb-{Guid.NewGuid():N}.png");
                try
                {
                    await Task.Run(() => _bundles.ExtractTexturePng(bundlePath, tempPath), cancellationToken)
                        .ConfigureAwait(false);

                    var bmp = LoadThumbnail(tempPath);
                    _cache[key] = bmp;
                    return bmp;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return _placeholder;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static BitmapImage LoadThumbnail(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 96;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static ImageSource CreatePlaceholder()
    {
        const int w = 72;
        const int h = 104;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1A)),
                new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x46, 0x52)), 1),
                new System.Windows.Rect(0.5, 0.5, w - 1, h - 1));
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    public void Dispose()
    {
        _gate.Dispose();
        _bundles.Dispose();
    }
}
