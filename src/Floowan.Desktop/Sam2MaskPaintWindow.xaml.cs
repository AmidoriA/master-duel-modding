using System.IO;
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

/// <summary>How the editor resolved a confirmed selection for the parent window.</summary>
public enum Sam2EditorResultKind
{
    /// <summary>Amber paint mask — parent runs SAM on the painted region.</summary>
    PaintPrompt,

    /// <summary>
    /// Click-mode working selection mask — parent <b>replaces</b> the subject mask
    /// (already includes prior-session subject plus add/remove edits).
    /// </summary>
    ClickSamMask
}

/// <summary>
/// SAM selection editor over card art.
/// <list type="bullet">
/// <item><b>Paint</b> — left-drag paint / right-drag erase; Apply returns a paint prompt mask.</item>
/// <item><b>Click object</b> — working selection starts from existing subject; left-click unions
/// SAM, right-click subtracts SAM; Apply returns the working mask.</item>
/// </list>
/// </summary>
public partial class Sam2MaskPaintWindow : Window
{
    private readonly Image<Rgba32> _art;
    private readonly string _artPath;
    private readonly Sam2PointCutoutService _sam2;
    private readonly Image<L8> _paintMask;
    private readonly WriteableBitmap _overlayBitmap;
    private readonly byte[] _overlayPixels;
    private readonly int _overlayStride;
    /// <summary>Snapshot of subject when the editor opened (may be null).</summary>
    private readonly Image<L8>? _baselineExistingMask;
    private readonly bool _hadExistingSubject;
    private Image<L8> _workingClickMask;
    private bool _painting;
    private bool _erasing;
    private bool _clickBusy;
    private int _brushRadius = 14;
    private double _displayScale = 1;
    private int _clickGeneration;

    public Sam2EditorResultKind ResultKind { get; private set; } = Sam2EditorResultKind.PaintPrompt;

    /// <summary>Paint-mode prompt mask (caller disposes). Null unless Apply in Paint mode.</summary>
    public Image<L8>? ResultPaintMask { get; private set; }

    /// <summary>Click-mode working SAM mask (caller disposes). Null unless Apply in Click mode.</summary>
    public Image<L8>? ResultSamMask { get; private set; }

    /// <summary>Cleaned RGB source matching <see cref="ResultSamMask"/> (caller disposes).</summary>
    public Image<Rgba32>? ResultSamSource { get; private set; }

    private bool IsClickMode => ClickModeRadio.IsChecked == true;

    public Sam2MaskPaintWindow(
        Image<Rgba32> art,
        string artPath,
        Sam2PointCutoutService sam2,
        Image<L8>? existingSubjectMask = null)
    {
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(sam2);
        if (string.IsNullOrWhiteSpace(artPath))
            throw new ArgumentException("Art path is required for Click-mode SAM.", nameof(artPath));

        InitializeComponent();
        _art = art.Clone();
        _artPath = artPath;
        _sam2 = sam2;
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

        if (existingSubjectMask is not null
            && existingSubjectMask.Width == _art.Width
            && existingSubjectMask.Height == _art.Height)
        {
            _baselineExistingMask = existingSubjectMask.Clone();
            _workingClickMask = existingSubjectMask.Clone();
            _hadExistingSubject = true;
        }
        else
        {
            _baselineExistingMask = null;
            _workingClickMask = new Image<L8>(_art.Width, _art.Height);
            _hadExistingSubject = false;
        }

        ArtImage.Source = ToBitmap(_art);
        MaskOverlay.Source = _overlayBitmap;
        ApplyExistingSubjectHighlight(_baselineExistingMask);
        RefreshWorkingClickOverlay();
        UpdateBrushLabel();
        SyncModeUi();
        SizeChanged += (_, _) => UpdateDisplayScale();
        Loaded += (_, _) =>
        {
            UpdateDisplayScale();
            UpdateBrushCursorVisualSize();
        };
        Closed += (_, _) => CleanupOwnedImages();
    }

    /// <summary>
    /// Loads cleaned illustration from a PNG path. Models should already be ensured by the caller.
    /// </summary>
    public static Sam2MaskPaintWindow FromImagePath(
        string imagePath,
        Sam2PointCutoutService sam2,
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

            return new Sam2MaskPaintWindow(clean, imagePath, sam2, highlight);
        }
        finally
        {
            if (!ReferenceEquals(loaded, clean))
                clean.Dispose();
        }
    }

    private void CleanupOwnedImages()
    {
        if (!ReferenceEquals(ResultPaintMask, _paintMask))
            _paintMask.Dispose();
        if (!ReferenceEquals(ResultSamMask, _workingClickMask))
            _workingClickMask.Dispose();
        _baselineExistingMask?.Dispose();
        _art.Dispose();
    }

    private void ApplyExistingSubjectHighlight(Image<L8>? existingSubjectMask)
    {
        if (existingSubjectMask is null)
        {
            ExistingSubjectOverlay.Visibility = Visibility.Collapsed;
            ExistingSubjectOverlay.Source = null;
            return;
        }

        ExistingSubjectOverlay.Source = ToMaskHighlightBitmap(
            existingSubjectMask,
            b: 70,
            g: 210,
            r: 40,
            a: 150);
        ExistingSubjectOverlay.Visibility = Visibility.Visible;
    }

    private void RefreshWorkingClickOverlay()
    {
        if (CountOpaque(_workingClickMask) == 0)
        {
            ClickPreviewOverlay.Source = null;
            ClickPreviewOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        ClickPreviewOverlay.Source = ToMaskHighlightBitmap(
            _workingClickMask,
            b: 230,
            g: 200,
            r: 40,
            a: 170);
        ClickPreviewOverlay.Visibility = Visibility.Visible;
    }

    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        SyncModeUi();
    }

    private void SyncModeUi()
    {
        var click = IsClickMode;
        PaintToolsPanel.Visibility = click ? Visibility.Collapsed : Visibility.Visible;
        ClickToolsPanel.Visibility = click ? Visibility.Visible : Visibility.Collapsed;
        MaskOverlay.Visibility = click ? Visibility.Collapsed : Visibility.Visible;
        BrushCursor.Visibility = Visibility.Collapsed;
        PaintHost.Cursor = click ? Cursors.Cross : Cursors.None;

        if (click)
        {
            // Working cyan overlay covers prior + edits; hide static green to avoid double-tint.
            ExistingSubjectOverlay.Visibility = Visibility.Collapsed;
            HelpText.Text =
                "Cyan = working selection (starts from prior subject). Left-click adds, right-click removes. Apply writes it back.";
            StatusText.Text = CountOpaque(_workingClickMask) == 0
                ? "Click object: left-click to add, right-click to remove."
                : "Left-click adds to selection, right-click removes (incl. prior session), then Apply.";
            RefreshWorkingClickOverlay();
        }
        else
        {
            ApplyExistingSubjectHighlight(_baselineExistingMask);
            HelpText.Text =
                "Paint (left-drag) or erase (right-drag). Green = already in Card Art; amber = new paint. Esc cancels.";
            StatusText.Text = _hadExistingSubject
                ? "Green = already added. Left-drag paints, right-drag erases, then Apply."
                : "Left-drag paints, right-drag erases. Apply when ready.";
            ClickPreviewOverlay.Visibility = Visibility.Collapsed;
        }
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
        if (IsClickMode || _clickBusy)
        {
            BrushCursor.Visibility = Visibility.Collapsed;
            return;
        }

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
        FlushPaintOverlay();
        StatusText.Text = _hadExistingSubject
            ? "Paint cleared (green already-added kept). Left-drag paints, right-drag erases."
            : "Paint cleared. Left-drag paints, right-drag erases, then Apply.";
    }

    private void ClearClickPreview_Click(object sender, RoutedEventArgs e)
    {
        _clickGeneration++;
        ResetWorkingClickMaskToBaseline();
        RefreshWorkingClickOverlay();
        StatusText.Text = _hadExistingSubject
            ? "Selection reset to prior-session subject. Left-click adds, right-click removes."
            : "Selection cleared. Left-click adds, right-click removes.";
    }

    private void ResetWorkingClickMaskToBaseline()
    {
        _workingClickMask.Dispose();
        _workingClickMask = _baselineExistingMask is not null
            ? _baselineExistingMask.Clone()
            : new Image<L8>(_art.Width, _art.Height);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_clickBusy)
        {
            StatusText.Text = "Wait for SAM 2 to finish, then Apply.";
            return;
        }

        if (IsClickMode)
        {
            // Allow Apply even when empty if we started with a subject (user removed everything).
            if (CountOpaque(_workingClickMask) == 0 && !_hadExistingSubject)
            {
                StatusText.Text = "Left-click an object to add to the selection first.";
                MessageBox.Show(
                    this,
                    "Left-click an object to add it to the working selection, then Apply.",
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ResultKind = Sam2EditorResultKind.ClickSamMask;
            ResultSamMask = _workingClickMask;
            ResultSamSource = _art.Clone();
            // Ownership of working mask transferred; leave a stub so Cleanup is safe.
            _workingClickMask = new Image<L8>(_art.Width, _art.Height);
            DialogResult = true;
            Close();
            return;
        }

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

        ResultKind = Sam2EditorResultKind.PaintPrompt;
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
        if (!IsClickMode)
            UpdateBrushCursorPosition(e.GetPosition(PaintHost));
    }

    private async void PaintHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickBusy)
            return;

        if (IsClickMode)
        {
            e.Handled = true;
            await RunClickSamAtAsync(e.GetPosition(PaintHost), subtract: false);
            return;
        }

        if (_erasing)
            return;

        UpdateBrushCursorPosition(e.GetPosition(PaintHost));
        if (!TryStrokeAt(e.GetPosition(PaintHost), erase: false))
            return;

        _painting = true;
        PaintHost.CaptureMouse();
        e.Handled = true;
    }

    private async void PaintHost_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickBusy)
            return;

        if (IsClickMode)
        {
            e.Handled = true;
            await RunClickSamAtAsync(e.GetPosition(PaintHost), subtract: true);
            return;
        }

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
        if (!IsClickMode)
            UpdateBrushCursorPosition(pos);

        if (IsClickMode || _clickBusy)
            return;

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
        StatusText.Text = _hadExistingSubject
            ? "Paint more (left) or erase (right), then Apply. Green = already added."
            : "Paint more (left) or erase (right), then Apply.";
    }

    private async Task RunClickSamAtAsync(System.Windows.Point hostPos, bool subtract)
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
            StatusText.Text = "Click inside the card art.";
            return;
        }

        var generation = ++_clickGeneration;
        _clickBusy = true;
        ApplyButton.IsEnabled = false;
        ClearClickPreviewButton.IsEnabled = false;
        PaintModeRadio.IsEnabled = false;
        ClickModeRadio.IsEnabled = false;
        Cursor = Cursors.Wait;
        var verb = subtract ? "removing" : "adding";
        StatusText.Text = $"SAM 2: {verb} at ({imageX:0},{imageY:0})…";

        try
        {
            if (!File.Exists(_artPath))
                throw new FileNotFoundException("Card art temp file was removed.", _artPath);

            var progress = new Progress<string>(msg =>
            {
                if (generation == _clickGeneration)
                    StatusText.Text = msg;
            });

            var prepared = await _sam2.PrepareSubjectWithPointAsync(
                _artPath,
                imageX,
                imageY,
                progress);

            if (generation != _clickGeneration)
            {
                prepared.Source.Dispose();
                prepared.Mask.Dispose();
                return;
            }

            Image<L8> next;
            if (subtract)
                next = Sam2PointCutoutService.SubtractMasks(_workingClickMask, prepared.Mask);
            else
                next = Sam2PointCutoutService.UnionMasks(_workingClickMask, prepared.Mask);

            prepared.Mask.Dispose();
            prepared.Source.Dispose();
            _workingClickMask.Dispose();
            _workingClickMask = next;
            RefreshWorkingClickOverlay();

            StatusText.Text = subtract
                ? $"Removed SAM region at ({imageX:0},{imageY:0}). Left-click adds, right-click removes, or Apply."
                : $"Added SAM region at ({imageX:0},{imageY:0}). Left-click adds, right-click removes, or Apply.";
        }
        catch (Exception ex)
        {
            if (generation == _clickGeneration)
            {
                StatusText.Text = "SAM 2 click failed: " + ex.Message;
                MessageBox.Show(
                    this,
                    "Could not run SAM 2 on that point:\n\n" + ex.Message,
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            if (generation == _clickGeneration)
            {
                _clickBusy = false;
                ApplyButton.IsEnabled = true;
                ClearClickPreviewButton.IsEnabled = true;
                PaintModeRadio.IsEnabled = true;
                ClickModeRadio.IsEnabled = true;
                Cursor = Cursors.Arrow;
                PaintHost.Cursor = Cursors.Cross;
            }
        }
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
        FlushPaintOverlay();
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
                    _overlayPixels[i] = 40;
                    _overlayPixels[i + 1] = 190;
                    _overlayPixels[i + 2] = 255;
                    _overlayPixels[i + 3] = 180;
                }
            }
        }
    }

    private void FlushPaintOverlay()
    {
        _overlayBitmap.WritePixels(
            new Int32Rect(0, 0, _art.Width, _art.Height),
            _overlayPixels,
            _overlayStride,
            0);
    }

    private static int CountOpaque(Image<L8> mask)
    {
        var keep = 0;
        var threshold = OverFrameAutoArtComposer.MaskKeepThreshold;
        for (var y = 0; y < mask.Height; y++)
        {
            var row = mask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < row.Length; x++)
            {
                if (row[x].PackedValue >= threshold)
                    keep++;
            }
        }

        return keep;
    }

    private static BitmapSource ToMaskHighlightBitmap(Image<L8> mask, byte b, byte g, byte r, byte a)
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
                if (row[x].PackedValue >= threshold)
                {
                    pixels[dest++] = b;
                    pixels[dest++] = g;
                    pixels[dest++] = r;
                    pixels[dest++] = a;
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
