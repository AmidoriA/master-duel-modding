using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Floowan.Core.Assets;
using Floowan.Core.Game;
using Floowan.Core.Imaging;
using Floowan.Core.Models;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace Floowan.Desktop;

/// <summary>
/// Loads and caches small card-art previews for thumbnail list views.
/// Over-frame (704×1024 / <see cref="CardRecord.IsOverframe"/>) thumbs show the full
/// foil-flattened canvas so overflow outside the art hole is visible.
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

    /// <summary>
    /// Cache key includes an OF marker so normal illustration thumbs and full-canvas
    /// OF thumbs never collide when a card's over-frame state changes.
    /// </summary>
    public static string CacheKey(CardRecord card) =>
        $"{card.Id}\u001f{card.Bundle}\u001f{(card.IsOverframe ? "of" : "n")}";

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

    public void Invalidate(CardRecord card)
    {
        // Drop both OF and normal keys — apply/restore may flip IsOverframe.
        _cache.TryRemove($"{card.Id}\u001f{card.Bundle}\u001fof", out _);
        _cache.TryRemove($"{card.Id}\u001f{card.Bundle}\u001fn", out _);
    }

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

                    var bmp = await Task.Run(() => LoadThumbnail(tempPath, card.IsOverframe), cancellationToken)
                        .ConfigureAwait(false);
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

    /// <summary>
    /// Normal arts: decode a small illustration thumb.
    /// OF / 704×1024: foil-flatten the full canvas (same strategy as OF live preview)
    /// so overflow above/beside the frame remains visible at Uniform stretch.
    /// </summary>
    private static ImageSource LoadThumbnail(string path, bool preferOverframe)
    {
        var identity = ImageSharpImage.Identify(path);
        var isOverframeCanvas = preferOverframe
            || (identity is not null
                && OverFrameAutoArtComposer.IsOverFrameTextureSize(identity.Width, identity.Height));

        if (isOverframeCanvas)
            return LoadOverframeThumbnail(path);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 96;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Full 704×1024 OF face with foil-mask alpha raised for WPF visibility.
    /// Decode width keeps the tall card aspect (overflow not cropped to the art hole).
    /// </summary>
    private static ImageSource LoadOverframeThumbnail(string path)
    {
        using var image = ImageSharpImage.Load<Rgba32>(path);
        using var flat = OverFrameAutoArtComposer.FlattenFoilMaskForPreview(image);
        using var ms = new MemoryStream();
        flat.Save(ms, new PngEncoder());
        ms.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        // ~704:1024 → ~96×140; Uniform in the 72×104 slot shows full-canvas overflow.
        bmp.DecodePixelWidth = 96;
        bmp.StreamSource = ms;
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
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x12, 0x16, 0x1A)),
                new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x46, 0x52)), 1),
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
