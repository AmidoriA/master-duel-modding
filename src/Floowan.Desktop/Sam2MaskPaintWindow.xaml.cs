using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace Floowan.Desktop;

/// <summary>
/// Brush editor over card art. On Apply, returns an L8 paint mask in image pixels
/// for <see cref="Sam2PointCutoutService.PrepareSubjectWithPaintedRegionAsync"/>.
/// </summary>
public partial class Sam2MaskPaintWindow : Window
{
    private readonly Image<Rgba32> _art;
    private readonly Image<L8> _paintMask;
    private readonly WriteableBitmap _overlayBitmap;
    private readonly byte[] _overlayPixels;
    private readonly int _overlayStride;
    private bool _painting;
    private int _brushRadius = 14;

    /// <summary>Paint mask in source image pixels (caller must dispose).</summary>
    public Image<L8>? ResultPaintMask { get; private set; }

    public Sam2MaskPaintWindow(Image<Rgba32> art)
    {
        ArgumentNullException.ThrowIfNull(art);
        InitializeComponent();
        _art = art.Clone();
        _paintMask = new Image<L8>(_art.Width, _art.Height);
        _overlayBitmap = new WriteableBitmap(
            _art.Width,
            _art.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        _overlayStride = _art.Width * 4;
        _overlayPixels = new byte[_overlayStride * _art.Height];
        ArtImage.Source = ToBitmap(_art);
        MaskOverlay.Source = _overlayBitmap;
        UpdateBrushLabel();
        Closed += (_, _) =>
        {
            if (!ReferenceEquals(ResultPaintMask, _paintMask))
                _paintMask.Dispose();
            _art.Dispose();
        };
    }

    /// <summary>
    /// Loads cleaned illustration from a PNG path for the paint editor.
    /// </summary>
    public static Sam2MaskPaintWindow FromImagePath(string imagePath)
    {
        using var loaded = ImageSharpImage.Load<Rgba32>(imagePath);
        var clean = OverFrameAutoArtComposer.RequireCleanIllustrationSource(loaded);
        try
        {
            return new Sam2MaskPaintWindow(clean);
        }
        finally
        {
            if (!ReferenceEquals(loaded, clean))
                clean.Dispose();
        }
    }

    private void BrushSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        var diameter = (int)Math.Round(BrushSizeSlider.Value);
        _brushRadius = Math.Max(diameter / 2, 1);
        UpdateBrushLabel();
    }

    private void UpdateBrushLabel()
    {
        var diameter = Math.Max(_brushRadius * 2, 1);
        BrushSizeValueText.Text = $"{diameter} px";
    }

    private void ClearMask_Click(object sender, RoutedEventArgs e)
    {
        for (var y = 0; y < _paintMask.Height; y++)
        {
            var row = _paintMask.DangerousGetPixelRowMemory(y).Span;
            row.Clear();
        }

        Array.Clear(_overlayPixels);
        FlushOverlay();
        StatusText.Text = "Mask cleared. Paint a region, then Apply.";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var prompts = Sam2PointCutoutService.BuildPromptsFromPaintMask(_paintMask);
        if (prompts.Count == 0)
        {
            StatusText.Text = "Paint a region before applying.";
            MessageBox.Show(
                this,
                "Paint over the subject area first, then click Apply selection.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ResultPaintMask = _paintMask.Clone();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        DialogResult = false;
        Close();
        e.Handled = true;
    }

    private void PaintHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryPaintAt(e.GetPosition(PaintHost)))
            return;

        _painting = true;
        PaintHost.CaptureMouse();
        e.Handled = true;
    }

    private void PaintHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_painting || e.LeftButton != MouseButtonState.Pressed)
            return;

        TryPaintAt(e.GetPosition(PaintHost));
    }

    private void PaintHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndPaint();
    }

    private void PaintHost_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_painting && e.LeftButton != MouseButtonState.Pressed)
            EndPaint();
    }

    private void PaintHost_LostMouseCapture(object sender, MouseEventArgs e)
    {
        EndPaint();
    }

    private void EndPaint()
    {
        if (!_painting)
            return;

        _painting = false;
        if (PaintHost.IsMouseCaptured)
            PaintHost.ReleaseMouseCapture();
        StatusText.Text = "Paint more, Clear mask, or Apply selection.";
    }

    private bool TryPaintAt(System.Windows.Point hostPos)
    {
        if (!Sam2PointCutoutService.TryMapPreviewClickToImage(
                hostPos.X,
                hostPos.Y,
                PaintHost.ActualWidth,
                PaintHost.ActualHeight,
                _art.Width,
                _art.Height,
                out var imageX,
                out var imageY))
        {
            return false;
        }

        StampBrush((int)MathF.Round(imageX), (int)MathF.Round(imageY));
        FlushOverlay();
        return true;
    }

    private void StampBrush(int cx, int cy)
    {
        var r = _brushRadius;
        var r2 = r * r;
        var w = _paintMask.Width;
        var h = _paintMask.Height;
        var minX = Math.Max(cx - r, 0);
        var maxX = Math.Min(cx + r, w - 1);
        var minY = Math.Max(cy - r, 0);
        var maxY = Math.Min(cy + r, h - 1);

        for (var y = minY; y <= maxY; y++)
        {
            var maskRow = _paintMask.DangerousGetPixelRowMemory(y).Span;
            var overlayRow = y * _overlayStride;
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if (dx * dx + dy * dy > r2)
                    continue;

                maskRow[x] = new L8(255);
                var i = overlayRow + x * 4;
                // Semi-transparent cyan overlay (B,G,R,A).
                _overlayPixels[i] = 220;
                _overlayPixels[i + 1] = 200;
                _overlayPixels[i + 2] = 40;
                _overlayPixels[i + 3] = 160;
            }
        }
    }

    private void FlushOverlay()
    {
        _overlayBitmap.WritePixels(
            new Int32Rect(0, 0, _art.Width, _art.Height),
            _overlayPixels,
            _overlayStride,
            0);
    }

    private static BitmapSource ToBitmap(Image<Rgba32> image)
    {
        var w = image.Width;
        var h = image.Height;
        var stride = w * 4;
        var pixels = new byte[stride * h];
        for (var y = 0; y < h; y++)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            var dest = y * stride;
            for (var x = 0; x < w; x++)
            {
                var px = row[x];
                pixels[dest++] = px.B;
                pixels[dest++] = px.G;
                pixels[dest++] = px.R;
                pixels[dest++] = px.A;
            }
        }

        var bmp = BitmapSource.Create(
            w,
            h,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bmp.Freeze();
        return bmp;
    }
}
