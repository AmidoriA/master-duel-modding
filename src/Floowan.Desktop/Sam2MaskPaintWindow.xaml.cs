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
/// Left-drag paints; right-drag erases. Optional existing-subject highlight (green).
/// </summary>
public partial class Sam2MaskPaintWindow : Window
{
    private readonly Image<Rgba32> _art;
    private readonly Image<L8> _paintMask;
    private readonly WriteableBitmap _overlayBitmap;
    private readonly byte[] _overlayPixels;
    private readonly int _overlayStride;
    private bool _painting;
    private bool _erasing;
    private int _brushRadius = 14;
    private double _displayScale = 1;

    /// <summary>Paint mask in source image pixels (caller must dispose).</summary>
    public Image<L8>? ResultPaintMask { get; private set; }

    public Sam2MaskPaintWindow(Image<Rgba32> art, Image<L8>? existingSubjectMask = null)
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
        ApplyExistingSubjectHighlight(existingSubjectMask);
        UpdateBrushLabel();
        SizeChanged += (_, _) => UpdateDisplayScale();
        Loaded += (_, _) =>
        {
            UpdateDisplayScale();
            UpdateBrushCursorVisualSize();
        };
        Closed += (_, _) =>
        {
            if (!ReferenceEquals(ResultPaintMask, _paintMask))
                _paintMask.Dispose();
            _art.Dispose();
        };
    }

    /// <summary>
    /// Loads cleaned illustration from a PNG path for the paint editor.
    /// Pass <paramref name="existingSubjectMask"/> (same pixel size as cleaned art) to
    /// show already-added Card Art subject as a green highlight.
    /// </summary>
    public static Sam2MaskPaintWindow FromImagePath(
        string imagePath,
        Image<L8>? existingSubjectMask = null)
    {
        using var loaded = ImageSharpImage.Load<Rgba32>(imagePath);
        var clean = OverFrameAutoArtComposer.RequireCleanIllustrationSource(loaded);
        try
        {
            Image<L8>? highlight = null;
            if (existingSubjectMask is not null
                && existingSubjectMask.Width == clean.Width
                && existingSubjectMask.Height == clean.Height)
            {
                highlight = existingSubjectMask;
            }

            return new Sam2MaskPaintWindow(clean, highlight);
        }
        finally
        {
            if (!ReferenceEquals(loaded, clean))
                clean.Dispose();
        }
    }

    private void ApplyExistingSubjectHighlight(Image<L8>? existingSubjectMask)
    {
        if (existingSubjectMask is null
            || existingSubjectMask.Width != _art.Width
            || existingSubjectMask.Height != _art.Height)
        {
            ExistingSubjectOverlay.Visibility = Visibility.Collapsed;
            ExistingSubjectOverlay.Source = null;
            return;
        }

        ExistingSubjectOverlay.Source = ToSubjectHighlightBitmap(existingSubjectMask);
        ExistingSubjectOverlay.Visibility = Visibility.Visible;
        StatusText.Text =
            "Green = already added. Left-drag paints more, right-drag erases paint, then Apply.";
    }

    private void BrushSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        var diameter = (int)Math.Round(BrushSizeSlider.Value);
        _brushRadius = Math.Max(diameter / 2, 1);
        UpdateBrushLabel();
        UpdateBrushCursorVisualSize();
    }

    private void UpdateBrushLabel()
    {
        var diameter = Math.Max(_brushRadius * 2, 1);
        BrushSizeValueText.Text = $"{diameter} px";
    }

    private void UpdateDisplayScale()
    {
        if (_art.Width <= 0 || _art.Height <= 0)
            return;

        var hostW = PaintHost.ActualWidth;
        var hostH = PaintHost.ActualHeight;
        if (hostW <= 0 || hostH <= 0)
            return;

        _displayScale = Math.Min(hostW / _art.Width, hostH / _art.Height);
        UpdateBrushCursorVisualSize();
    }

    private void UpdateBrushCursorVisualSize()
    {
        var diameterPx = Math.Max(_brushRadius * 2, 1);
        var screenDiameter = Math.Max(diameterPx * _displayScale, 4);
        BrushCursor.Width = screenDiameter;
        BrushCursor.Height = screenDiameter;
    }

    private void UpdateBrushCursorPosition(System.Windows.Point hostPos)
    {
        if (_displayScale <= 0)
            UpdateDisplayScale();

        BrushCursor.Margin = new Thickness(
            hostPos.X - BrushCursor.Width * 0.5,
            hostPos.Y - BrushCursor.Height * 0.5,
            0,
            0);
        BrushCursor.Visibility = Visibility.Visible;
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
        StatusText.Text = ExistingSubjectOverlay.Visibility == Visibility.Visible
            ? "Paint cleared (green already-added kept). Left-drag paints, right-drag erases."
            : "Paint cleared. Left-drag paints, right-drag erases, then Apply.";
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

    private void PaintHost_MouseEnter(object sender, MouseEventArgs e)
    {
        UpdateBrushCursorPosition(e.GetPosition(PaintHost));
    }

    private void PaintHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_erasing)
            return;

        UpdateBrushCursorPosition(e.GetPosition(PaintHost));
        if (!TryStrokeAt(e.GetPosition(PaintHost), erase: false))
            return;

        _painting = true;
        PaintHost.CaptureMouse();
        e.Handled = true;
    }

    private void PaintHost_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_painting)
            return;

        UpdateBrushCursorPosition(e.GetPosition(PaintHost));
        if (!TryStrokeAt(e.GetPosition(PaintHost), erase: true))
            return;

        _erasing = true;
        PaintHost.CaptureMouse();
        e.Handled = true;
    }

    private void PaintHost_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(PaintHost);
        UpdateBrushCursorPosition(pos);

        if (_painting && e.LeftButton == MouseButtonState.Pressed)
            TryStrokeAt(pos, erase: false);
        else if (_erasing && e.RightButton == MouseButtonState.Pressed)
            TryStrokeAt(pos, erase: true);
    }

    private void PaintHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_painting)
            EndStroke();
    }

    private void PaintHost_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_erasing)
            EndStroke();
    }

    private void PaintHost_MouseLeave(object sender, MouseEventArgs e)
    {
        BrushCursor.Visibility = Visibility.Collapsed;
        if ((_painting && e.LeftButton != MouseButtonState.Pressed)
            || (_erasing && e.RightButton != MouseButtonState.Pressed))
        {
            EndStroke();
        }
    }

    private void PaintHost_LostMouseCapture(object sender, MouseEventArgs e)
    {
        EndStroke();
    }

    private void EndStroke()
    {
        if (!_painting && !_erasing)
            return;

        _painting = false;
        _erasing = false;
        if (PaintHost.IsMouseCaptured)
            PaintHost.ReleaseMouseCapture();
        StatusText.Text = ExistingSubjectOverlay.Visibility == Visibility.Visible
            ? "Paint more (left) or erase (right), then Apply. Green = already added."
            : "Paint more (left) or erase (right), then Apply.";
    }

    private bool TryStrokeAt(System.Windows.Point hostPos, bool erase)
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

        StampBrush((int)MathF.Round(imageX), (int)MathF.Round(imageY), erase);
        FlushOverlay();
        return true;
    }

    private void StampBrush(int cx, int cy, bool erase)
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

                var i = overlayRow + x * 4;
                if (erase)
                {
                    maskRow[x] = new L8(0);
                    _overlayPixels[i] = 0;
                    _overlayPixels[i + 1] = 0;
                    _overlayPixels[i + 2] = 0;
                    _overlayPixels[i + 3] = 0;
                }
                else
                {
                    maskRow[x] = new L8(255);
                    // Amber/yellow paint — distinct from green existing-subject highlight.
                    _overlayPixels[i] = 40;   // B
                    _overlayPixels[i + 1] = 190; // G
                    _overlayPixels[i + 2] = 255; // R
                    _overlayPixels[i + 3] = 180; // A
                }
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

    private static BitmapSource ToSubjectHighlightBitmap(Image<L8> mask)
    {
        var w = mask.Width;
        var h = mask.Height;
        var stride = w * 4;
        var pixels = new byte[stride * h];
        var threshold = OverFrameAutoArtComposer.MaskKeepThreshold;
        for (var y = 0; y < h; y++)
        {
            var row = mask.DangerousGetPixelRowMemory(y).Span;
            var dest = y * stride;
            for (var x = 0; x < w; x++)
            {
                var a = row[x].PackedValue;
                if (a >= threshold)
                {
                    // Semi-transparent green fill for already-added subject.
                    pixels[dest++] = 70;  // B
                    pixels[dest++] = 210; // G
                    pixels[dest++] = 40;  // R
                    pixels[dest++] = 150; // A
                }
                else
                {
                    dest += 4;
                }
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
