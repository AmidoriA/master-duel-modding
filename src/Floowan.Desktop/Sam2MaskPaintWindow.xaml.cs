using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Floowan.Core.Imaging;
using Floowan.Desktop.Localization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using WpfPoint = System.Windows.Point;

namespace Floowan.Desktop;

/// <summary>How the editor resolved a confirmed selection for the parent window.</summary>
public enum Sam2EditorResultKind
{
    /// <summary>Legacy paint-prompt path (parent runs SAM). Prefer <see cref="WorkingSamMask"/>.</summary>
    PaintPrompt,

    /// <summary>
    /// Working selection mask — parent <b>replaces</b> the subject mask
    /// (prior-session subject plus Click / Lasso / Paint edits).
    /// </summary>
    WorkingSamMask,

    /// <summary>Alias kept for older call sites; same as <see cref="WorkingSamMask"/>.</summary>
    ClickSamMask = WorkingSamMask
}

/// <summary>
/// SAM selection editor: Click (default), Lasso, Paint — shared working selection + Undo.
/// </summary>
public partial class Sam2MaskPaintWindow : Window
{
    private const int MaxUndoLevels = 20;

    /// <summary>Working-selection highlight palette (BGRA overlay + display name).</summary>
    private static readonly SelectionHighlightColor[] SelectionPalette =
    [
        new("Cyan", B: 230, G: 200, R: 40, A: 170),
        new("Green", B: 70, G: 210, R: 40, A: 170),
        new("Magenta", B: 200, G: 60, R: 230, A: 170),
        new("Orange", B: 40, G: 140, R: 255, A: 170),
    ];

    /// <summary>Dim prior-session subject overlay (green, slightly softer than palette green).</summary>
    private static readonly SelectionHighlightColor ExistingSubjectHighlight =
        new("Prior", B: 70, G: 210, R: 40, A: 150);

    /// <summary>App-session preference for the active selection highlight (index into <see cref="SelectionPalette"/>).</summary>
    private static int SessionSelectionColorIndex;

    /// <summary>Default selection-overlay opacity percent (matches prior fixed ~50% Image.Opacity feel).</summary>
    private const int DefaultOverlayOpacityPercent = 50;

    /// <summary>App-session preference for working-selection / prior-subject overlay opacity (10–100).</summary>
    private static int SessionOverlayOpacityPercent = DefaultOverlayOpacityPercent;

    /// <summary>Soft rim width (image pixels) for paint-brush stamps — display/prompt only.</summary>
    private const float BrushAaRimPixels = 1.5f;

    private readonly Image<Rgba32> _art;
    private readonly string _artPath;
    private readonly Sam2PointCutoutService _sam2;
    private readonly Image<L8> _paintMask;
    private readonly WriteableBitmap _overlayBitmap;
    private readonly byte[] _overlayPixels;
    private readonly int _overlayStride;
    private readonly Image<L8>? _baselineExistingMask;
    private readonly bool _hadExistingSubject;
    private readonly List<Image<L8>> _undoStack = [];
    private readonly List<(float X, float Y)> _lassoImagePoints = [];
    private Image<L8> _workingClickMask;
    private bool _painting;
    private bool _erasing;
    private bool _lassoing;
    private bool _samBusy;
    private int _brushRadius = 14;
    private double _displayScale = 1;
    private int _samGeneration;
    private int _selectionColorIndex;

    private readonly record struct SelectionHighlightColor(string Name, byte B, byte G, byte R, byte A);

    public Sam2EditorResultKind ResultKind { get; private set; } = Sam2EditorResultKind.WorkingSamMask;
    public Image<L8>? ResultPaintMask { get; private set; }
    public Image<L8>? ResultSamMask { get; private set; }
    public Image<Rgba32>? ResultSamSource { get; private set; }

    private bool IsClickMode => ClickModeRadio.IsChecked == true;
    private bool IsLassoMode => LassoModeRadio.IsChecked == true;
    private bool IsPaintMode => PaintModeRadio.IsChecked == true;

    public Sam2MaskPaintWindow(
        Image<Rgba32> art,
        string artPath,
        Sam2PointCutoutService sam2,
        Image<L8>? existingSubjectMask = null)
    {
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(sam2);
        if (string.IsNullOrWhiteSpace(artPath))
            throw new ArgumentException("Art path is required for SAM.", nameof(artPath));

        InitializeComponent();
        Title = Loc.T("sam.title");
        _art = art.Clone();
        _artPath = artPath;
        _sam2 = sam2;
        _paintMask = new Image<L8>(_art.Width, _art.Height);
        _overlayBitmap = new WriteableBitmap(
            _art.Width, _art.Height, 96, 96, PixelFormats.Bgra32, null);
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
        ApplySessionHighlightColor();
        ApplySessionOverlayOpacity();
        ApplyExistingSubjectHighlight(_baselineExistingMask);
        RefreshWorkingClickOverlay();
        UpdateBrushLabel();
        UpdateUndoButton();
        SyncModeUi();
        SizeChanged += (_, _) => UpdateDisplayScale();
        Loaded += (_, _) =>
        {
            UpdateDisplayScale();
            UpdateBrushCursorVisualSize();
            SyncModeUi();
        };
        Closed += (_, _) => CleanupOwnedImages();
    }

    private SelectionHighlightColor ActiveSelectionColor =>
        SelectionPalette[Math.Clamp(_selectionColorIndex, 0, SelectionPalette.Length - 1)];

    private void ApplySessionHighlightColor()
    {
        _selectionColorIndex = Math.Clamp(SessionSelectionColorIndex, 0, SelectionPalette.Length - 1);
        var radios = new[]
        {
            ColorSwatchCyan,
            ColorSwatchGreen,
            ColorSwatchMagenta,
            ColorSwatchOrange,
        };
        radios[_selectionColorIndex].IsChecked = true;
    }

    private void HighlightColor_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton radio
            || !int.TryParse(radio.Tag?.ToString(), out var index)
            || index < 0
            || index >= SelectionPalette.Length)
        {
            return;
        }

        _selectionColorIndex = index;
        SessionSelectionColorIndex = index;
        RefreshWorkingClickOverlay();
        if (IsLoaded)
            StatusText.Text = $"Selection highlight: {ActiveSelectionColor.Name}.";
    }

    private void ApplySessionOverlayOpacity()
    {
        var percent = Math.Clamp(SessionOverlayOpacityPercent, 10, 100);
        SessionOverlayOpacityPercent = percent;
        OverlayOpacitySlider.Value = percent;
        ApplyOverlayOpacity(percent);
    }

    private void OverlayOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        var percent = (int)Math.Clamp(Math.Round(e.NewValue), 10, 100);
        SessionOverlayOpacityPercent = percent;
        ApplyOverlayOpacity(percent);
        StatusText.Text = $"Selection overlay opacity: {percent}%.";
    }

    private void ApplyOverlayOpacity(int percent)
    {
        var opacity = percent / 100.0;
        ClickPreviewOverlay.Opacity = opacity;
        ExistingSubjectOverlay.Opacity = opacity;
        OverlayOpacityValueText.Text = $"{percent}%";
    }

    public static Sam2MaskPaintWindow FromImagePath(
        string imagePath,
        Sam2PointCutoutService sam2,
        Image<L8>? existingSubjectMask = null)
    {
        using var loaded = ImageSharpImage.Load<Rgba32>(imagePath);
        var clean = OverFrameAutoArtComposer.RequireCleanIllustrationSource(loaded);
        Image<L8>? alignedOwned = null;
        try
        {
            Image<L8>? highlight = null;
            if (existingSubjectMask is not null)
            {
                if (existingSubjectMask.Width == clean.Width
                    && existingSubjectMask.Height == clean.Height)
                {
                    highlight = existingSubjectMask;
                }
                else
                {
                    // Restored Custom OF stages (or Pick manually PNGs) may differ from a
                    // freshly exported live illustration — align rather than silently drop.
                    alignedOwned = ResizeMaskTo(existingSubjectMask, clean.Width, clean.Height);
                    highlight = alignedOwned;
                }
            }

            return new Sam2MaskPaintWindow(clean, imagePath, sam2, highlight);
        }
        finally
        {
            alignedOwned?.Dispose();
            if (!ReferenceEquals(loaded, clean))
                clean.Dispose();
        }
    }

    /// <summary>
    /// Aligns an existing subject mask to the SAM art canvas (nearest-neighbor).
    /// Caller owns the returned image.
    /// </summary>
    public static Image<L8> ResizeMaskTo(Image<L8> mask, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Mask size must be positive.");

        if (mask.Width == width && mask.Height == height)
            return mask.Clone();

        return mask.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new SixLabors.ImageSharp.Size(width, height),
            Mode = SixLabors.ImageSharp.Processing.ResizeMode.Stretch,
            Sampler = KnownResamplers.NearestNeighbor
        }));
    }

    private void CleanupOwnedImages()
    {
        if (!ReferenceEquals(ResultPaintMask, _paintMask))
            _paintMask.Dispose();
        if (!ReferenceEquals(ResultSamMask, _workingClickMask))
            _workingClickMask.Dispose();
        _baselineExistingMask?.Dispose();
        foreach (var snap in _undoStack)
            snap.Dispose();
        _undoStack.Clear();
        _art.Dispose();
    }

    private void PushWorkingHistory()
    {
        _undoStack.Add(_workingClickMask.Clone());
        while (_undoStack.Count > MaxUndoLevels)
        {
            _undoStack[0].Dispose();
            _undoStack.RemoveAt(0);
        }

        UpdateUndoButton();
    }

    private void UpdateUndoButton()
    {
        UndoButton.IsEnabled = !_samBusy && _undoStack.Count > 0;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => TryUndo();

    private bool TryUndo()
    {
        if (_samBusy || _undoStack.Count == 0)
            return false;

        _workingClickMask.Dispose();
        _workingClickMask = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RefreshWorkingClickOverlay();
        UpdateUndoButton();
        StatusText.Text = _undoStack.Count == 0
            ? "Undid last change (history empty)."
            : $"Undid last change ({_undoStack.Count} step(s) left).";
        return true;
    }

    private void ApplyExistingSubjectHighlight(Image<L8>? existingSubjectMask)
    {
        if (existingSubjectMask is null)
        {
            ExistingSubjectOverlay.Visibility = Visibility.Collapsed;
            ExistingSubjectOverlay.Source = null;
            return;
        }

        var prior = ExistingSubjectHighlight;
        ExistingSubjectOverlay.Source = ToMaskHighlightBitmap(
            existingSubjectMask, prior.B, prior.G, prior.R, prior.A);
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

        var tint = ActiveSelectionColor;
        ClickPreviewOverlay.Source = ToMaskHighlightBitmap(
            _workingClickMask, tint.B, tint.G, tint.R, tint.A);
        ClickPreviewOverlay.Visibility = Visibility.Visible;
    }

    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        CancelLassoInProgress();
        ClearPaintPrompt();
        SyncModeUi();
    }

    private void SyncModeUi()
    {
        PaintToolsPanel.Visibility = IsPaintMode ? Visibility.Visible : Visibility.Collapsed;
        MaskOverlay.Visibility = IsPaintMode || IsLassoMode ? Visibility.Visible : Visibility.Collapsed;
        BrushCursor.Visibility = Visibility.Collapsed;
        LassoPolyline.Visibility = Visibility.Collapsed;
        ExistingSubjectOverlay.Visibility = Visibility.Collapsed;
        RefreshWorkingClickOverlay();

        if (IsClickMode)
        {
            PaintHost.Cursor = Cursors.Cross;
            HelpText.Text =
                "Click object (default): working selection tint. Left-click adds, right-click removes. Undo / Reset available.";
            StatusText.Text = CountOpaque(_workingClickMask) == 0
                ? "Click object: left-click to add, right-click to remove."
                : "Left-click adds, right-click removes (incl. prior session), Undo, or Apply.";
        }
        else if (IsLassoMode)
        {
            PaintHost.Cursor = Cursors.Cross;
            HelpText.Text =
                "Lasso: drag a freehand loop. On release the interior fills, SAM runs, and the result unions into the working selection.";
            StatusText.Text = "Lasso: drag around a region, release to run SAM 2.";
        }
        else
        {
            PaintHost.Cursor = Cursors.None;
            HelpText.Text =
                "Paint: amber brush prompt. On stroke end SAM unions into the working selection. Right-erase adjusts amber. Circle cursor = brush size.";
            StatusText.Text = CountOpaque(_workingClickMask) == 0
                ? "Paint a region, release to run SAM 2, then Apply."
                : "Paint more (release to add), erase amber, Undo, or Apply.";
        }
    }

    private void SetSamBusy(bool busy)
    {
        _samBusy = busy;
        ApplyButton.IsEnabled = !busy;
        UndoButton.IsEnabled = !busy && _undoStack.Count > 0;
        ResetSelectionButton.IsEnabled = !busy;
        ClearMaskButton.IsEnabled = !busy;
        ClickModeRadio.IsEnabled = !busy;
        LassoModeRadio.IsEnabled = !busy;
        PaintModeRadio.IsEnabled = !busy;
        ColorSwatchCyan.IsEnabled = !busy;
        ColorSwatchGreen.IsEnabled = !busy;
        ColorSwatchMagenta.IsEnabled = !busy;
        ColorSwatchOrange.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    private void BrushSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        _brushRadius = Math.Max((int)Math.Round(BrushSizeSlider.Value) / 2, 1);
        UpdateBrushLabel();
        UpdateBrushCursorVisualSize();
    }

    private void UpdateBrushLabel() =>
        BrushSizeValueText.Text = $"{Math.Max(_brushRadius * 2, 1)} px";

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
        var screenDiameter = Math.Max(Math.Max(_brushRadius * 2, 1) * _displayScale, 4);
        BrushCursor.Width = screenDiameter;
        BrushCursor.Height = screenDiameter;
    }

    private void UpdateBrushCursorPosition(WpfPoint hostPos)
    {
        if (!IsPaintMode || _samBusy)
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
        _samGeneration++;
        ClearPaintPrompt();
        StatusText.Text = "Paint prompt cleared (working selection kept).";
    }

    private void ClearPaintPrompt()
    {
        for (var y = 0; y < _paintMask.Height; y++)
            _paintMask.DangerousGetPixelRowMemory(y).Span.Clear();
        Array.Clear(_overlayPixels);
        FlushPaintOverlay();
    }

    private void ClearClickPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_samBusy)
            return;

        _samGeneration++;
        CancelLassoInProgress();
        ClearPaintPrompt();
        PushWorkingHistory();
        ResetWorkingClickMaskToBaseline();
        RefreshWorkingClickOverlay();
        StatusText.Text = _hadExistingSubject
            ? "Selection reset to prior-session subject."
            : "Selection cleared.";
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
        if (_samBusy)
        {
            StatusText.Text = "Wait for SAM 2 to finish, then Apply.";
            return;
        }

        if (_lassoing || CountOpaque(_paintMask) > 0)
        {
            StatusText.Text = "Finish the current stroke (SAM runs on release), then Apply.";
            MessageBox.Show(
                this,
                "Finish the current lasso/paint stroke before Apply.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (CountOpaque(_workingClickMask) == 0 && !_hadExistingSubject)
        {
            StatusText.Text = "Add a selection first (Click, Lasso, or Paint).";
            MessageBox.Show(
                this,
                "Add to the working selection with Click, Lasso, or Paint, then Apply.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ResultKind = Sam2EditorResultKind.WorkingSamMask;
        ResultSamMask = _workingClickMask;
        ResultSamSource = _art.Clone();
        _workingClickMask = new Image<L8>(_art.Width, _art.Height);
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
        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (TryUndo())
                e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape)
            return;

        DialogResult = false;
        Close();
        e.Handled = true;
    }

    private void PaintHost_MouseEnter(object sender, MouseEventArgs e)
    {
        if (IsPaintMode)
            UpdateBrushCursorPosition(e.GetPosition(PaintHost));
    }

    private async void PaintHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_samBusy)
            return;

        if (IsClickMode)
        {
            e.Handled = true;
            await RunClickSamAtAsync(e.GetPosition(PaintHost), subtract: false);
            return;
        }

        if (IsLassoMode)
        {
            if (_erasing || _painting)
                return;
            BeginLasso(e.GetPosition(PaintHost));
            e.Handled = true;
            return;
        }

        // Paint
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
        if (_samBusy)
            return;

        if (IsClickMode)
        {
            e.Handled = true;
            await RunClickSamAtAsync(e.GetPosition(PaintHost), subtract: true);
            return;
        }

        if (IsLassoMode || _painting || _lassoing)
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
        if (IsPaintMode)
            UpdateBrushCursorPosition(pos);

        if (_samBusy)
            return;

        if (_lassoing && e.LeftButton == MouseButtonState.Pressed)
        {
            ContinueLasso(pos);
            return;
        }

        if (IsClickMode)
            return;

        if (_painting && e.LeftButton == MouseButtonState.Pressed)
            TryStrokeAt(pos, erase: false);
        else if (_erasing && e.RightButton == MouseButtonState.Pressed)
            TryStrokeAt(pos, erase: true);
    }

    private void PaintHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_lassoing)
            _ = EndLassoAsync();
        else if (_painting)
            _ = EndStrokeAsync();
    }

    private void PaintHost_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_erasing)
            _ = EndStrokeAsync();
    }

    private void PaintHost_MouseLeave(object sender, MouseEventArgs e)
    {
        BrushCursor.Visibility = Visibility.Collapsed;
        if (_lassoing && e.LeftButton != MouseButtonState.Pressed)
            _ = EndLassoAsync();
        else if ((_painting && e.LeftButton != MouseButtonState.Pressed)
                 || (_erasing && e.RightButton != MouseButtonState.Pressed))
            _ = EndStrokeAsync();
    }

    private void PaintHost_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_lassoing)
            _ = EndLassoAsync();
        else if (_painting || _erasing)
            _ = EndStrokeAsync();
    }

    private void BeginLasso(WpfPoint hostPos)
    {
        if (!TryMapHostToImage(hostPos, out var ix, out var iy))
            return;

        CancelLassoInProgress();
        _lassoing = true;
        _lassoImagePoints.Add((ix, iy));
        LassoPolyline.Points = [hostPos];
        LassoPolyline.Visibility = Visibility.Visible;
        PaintHost.CaptureMouse();
        StatusText.Text = "Lasso: drag to enclose a region…";
    }

    private void ContinueLasso(WpfPoint hostPos)
    {
        if (!TryMapHostToImage(hostPos, out var ix, out var iy))
            return;

        if (_lassoImagePoints.Count > 0)
        {
            var (lx, ly) = _lassoImagePoints[^1];
            var dx = ix - lx;
            var dy = iy - ly;
            if (dx * dx + dy * dy < 2.5f)
                return;
        }

        _lassoImagePoints.Add((ix, iy));
        LassoPolyline.Points.Add(hostPos);
    }

    private void CancelLassoInProgress()
    {
        _lassoing = false;
        _lassoImagePoints.Clear();
        LassoPolyline.Points.Clear();
        LassoPolyline.Visibility = Visibility.Collapsed;
        if (PaintHost.IsMouseCaptured && !_painting && !_erasing)
            PaintHost.ReleaseMouseCapture();
    }

    private async Task EndLassoAsync()
    {
        if (!_lassoing)
            return;

        _lassoing = false;
        if (PaintHost.IsMouseCaptured)
            PaintHost.ReleaseMouseCapture();

        var points = _lassoImagePoints.ToArray();
        _lassoImagePoints.Clear();
        LassoPolyline.Points.Clear();
        LassoPolyline.Visibility = Visibility.Collapsed;

        if (points.Length < 3)
        {
            StatusText.Text = "Lasso needs a closed loop — drag a larger path.";
            return;
        }

        ClearPaintPrompt();
        FillPolygonIntoPaintMask(points);
        SoftenHighlightEdgePixels(_overlayPixels, _art.Width, _art.Height, _overlayStride);
        FlushPaintOverlay();
        if (CountOpaque(_paintMask) == 0)
        {
            StatusText.Text = "Lasso interior was empty — try again.";
            return;
        }

        await RunLiveRegionSamAsync("lasso");
    }

    private async Task EndStrokeAsync()
    {
        if (!_painting && !_erasing)
            return;

        _painting = false;
        _erasing = false;
        if (PaintHost.IsMouseCaptured)
            PaintHost.ReleaseMouseCapture();

        if (!IsPaintMode || _samBusy)
            return;

        await RunLiveRegionSamAsync("paint");
    }

    private async Task RunLiveRegionSamAsync(string sourceLabel)
    {
        if (CountOpaque(_paintMask) == 0)
        {
            StatusText.Text = CountOpaque(_workingClickMask) == 0
                ? $"Draw a {sourceLabel} region, release to run SAM 2."
                : $"{sourceLabel} prompt empty. Continue or Apply.";
            return;
        }

        var generation = ++_samGeneration;
        SetSamBusy(true);
        StatusText.Text = $"SAM 2: segmenting {sourceLabel} region…";

        Image<L8>? promptClone = null;
        try
        {
            if (!File.Exists(_artPath))
                throw new FileNotFoundException("Card art temp file was removed.", _artPath);

            promptClone = _paintMask.Clone();
            var progress = new Progress<string>(msg =>
            {
                if (generation == _samGeneration)
                    StatusText.Text = msg;
            });

            var prepared = await _sam2.PrepareSubjectWithPaintedRegionAsync(
                _artPath, promptClone, progress);

            if (generation != _samGeneration)
            {
                prepared.Source.Dispose();
                prepared.Mask.Dispose();
                return;
            }

            PushWorkingHistory();
            var next = Sam2PointCutoutService.UnionMasks(_workingClickMask, prepared.Mask);
            prepared.Mask.Dispose();
            prepared.Source.Dispose();
            _workingClickMask.Dispose();
            _workingClickMask = next;
            ClearPaintPrompt();
            RefreshWorkingClickOverlay();
            StatusText.Text =
                $"Selection updated from {sourceLabel}. Continue, Undo, or Apply.";
        }
        catch (Exception ex)
        {
            if (generation == _samGeneration)
            {
                StatusText.Text = $"SAM 2 {sourceLabel} failed: " + ex.Message;
                MessageBox.Show(
                    this,
                    $"Could not run SAM 2 on the {sourceLabel} region:\n\n" + ex.Message,
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            promptClone?.Dispose();
            if (generation == _samGeneration)
            {
                SetSamBusy(false);
                if (IsPaintMode)
                    PaintHost.Cursor = Cursors.None;
                else
                    PaintHost.Cursor = Cursors.Cross;
            }
        }
    }

    private async Task RunClickSamAtAsync(WpfPoint hostPos, bool subtract)
    {
        if (!TryMapHostToImage(hostPos, out var imageX, out var imageY))
        {
            StatusText.Text = "Click inside the card art.";
            return;
        }

        var generation = ++_samGeneration;
        SetSamBusy(true);
        var verb = subtract ? "removing" : "adding";
        StatusText.Text = $"SAM 2: {verb} at ({imageX:0},{imageY:0})…";

        try
        {
            if (!File.Exists(_artPath))
                throw new FileNotFoundException("Card art temp file was removed.", _artPath);

            var progress = new Progress<string>(msg =>
            {
                if (generation == _samGeneration)
                    StatusText.Text = msg;
            });

            var prepared = await _sam2.PrepareSubjectWithPointAsync(
                _artPath, imageX, imageY, progress);

            if (generation != _samGeneration)
            {
                prepared.Source.Dispose();
                prepared.Mask.Dispose();
                return;
            }

            PushWorkingHistory();
            var next = subtract
                ? Sam2PointCutoutService.SubtractMasks(_workingClickMask, prepared.Mask)
                : Sam2PointCutoutService.UnionMasks(_workingClickMask, prepared.Mask);
            prepared.Mask.Dispose();
            prepared.Source.Dispose();
            _workingClickMask.Dispose();
            _workingClickMask = next;
            RefreshWorkingClickOverlay();
            StatusText.Text = subtract
                ? $"Removed at ({imageX:0},{imageY:0}). Continue, Undo, or Apply."
                : $"Added at ({imageX:0},{imageY:0}). Continue, Undo, or Apply.";
        }
        catch (Exception ex)
        {
            if (generation == _samGeneration)
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
            if (generation == _samGeneration)
            {
                SetSamBusy(false);
                PaintHost.Cursor = Cursors.Cross;
            }
        }
    }

    private bool TryMapHostToImage(WpfPoint hostPos, out float imageX, out float imageY) =>
        Sam2PointCutoutService.TryMapPreviewClickToImage(
            hostPos.X, hostPos.Y,
            PaintHost.ActualWidth, PaintHost.ActualHeight,
            _art.Width, _art.Height,
            out imageX, out imageY);

    private bool TryStrokeAt(WpfPoint hostPos, bool erase)
    {
        if (!TryMapHostToImage(hostPos, out var imageX, out var imageY))
            return false;
        StampBrush((int)MathF.Round(imageX), (int)MathF.Round(imageY), erase);
        FlushPaintOverlay();
        return true;
    }

    /// <summary>
    /// Soft-rim brush (~1.5 px AA) for paint-prompt overlay. Soft edges are display/prompt
    /// only — OF composition still binarizes subject masks at <see cref="OverFrameAutoArtComposer.MaskKeepThreshold"/>.
    /// </summary>
    private void StampBrush(int cx, int cy, bool erase)
    {
        var r = _brushRadius;
        // Include a half-pixel fringe so the outer AA samples land inside the stamp bounds.
        var searchR = r + 1;
        var solidR = Math.Max(r - BrushAaRimPixels, 0f);
        var falloff = Math.Max(r - solidR, 0.001f);
        var minX = Math.Max(cx - searchR, 0);
        var maxX = Math.Min(cx + searchR, _paintMask.Width - 1);
        var minY = Math.Max(cy - searchR, 0);
        var maxY = Math.Min(cy + searchR, _paintMask.Height - 1);

        for (var y = minY; y <= maxY; y++)
        {
            var maskRow = _paintMask.DangerousGetPixelRowMemory(y).Span;
            var overlayRow = y * _overlayStride;
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > r)
                    continue;

                var coverage = dist <= solidR
                    ? 1f
                    : 1f - (dist - solidR) / falloff;
                coverage = Math.Clamp(coverage, 0f, 1f);
                if (coverage <= 0f)
                    continue;

                var i = overlayRow + x * 4;
                if (erase)
                {
                    var keep = 1f - coverage;
                    var next = (byte)Math.Clamp((int)MathF.Round(maskRow[x].PackedValue * keep), 0, 255);
                    maskRow[x] = new L8(next);
                    if (next == 0)
                    {
                        _overlayPixels[i] = 0;
                        _overlayPixels[i + 1] = 0;
                        _overlayPixels[i + 2] = 0;
                        _overlayPixels[i + 3] = 0;
                    }
                    else
                    {
                        _overlayPixels[i] = 40;
                        _overlayPixels[i + 1] = 190;
                        _overlayPixels[i + 2] = 255;
                        _overlayPixels[i + 3] = (byte)Math.Clamp((int)MathF.Round(180f * next / 255f), 0, 255);
                    }
                }
                else
                {
                    var painted = (byte)Math.Clamp((int)MathF.Round(255f * coverage), 0, 255);
                    if (painted > maskRow[x].PackedValue)
                        maskRow[x] = new L8(painted);

                    var overlayA = (byte)Math.Clamp((int)MathF.Round(180f * maskRow[x].PackedValue / 255f), 0, 255);
                    _overlayPixels[i] = 40;
                    _overlayPixels[i + 1] = 190;
                    _overlayPixels[i + 2] = 255;
                    _overlayPixels[i + 3] = overlayA;
                }
            }
        }
    }

    /// <summary>Scanline fill of a closed polygon into the amber paint prompt mask.</summary>
    private void FillPolygonIntoPaintMask(IReadOnlyList<(float X, float Y)> points)
    {
        var n = points.Count;
        if (n < 3)
            return;

        var minY = (int)Math.Floor(points.Min(p => p.Y));
        var maxY = (int)Math.Ceiling(points.Max(p => p.Y));
        minY = Math.Clamp(minY, 0, _paintMask.Height - 1);
        maxY = Math.Clamp(maxY, 0, _paintMask.Height - 1);

        for (var y = minY; y <= maxY; y++)
        {
            var crossings = new List<float>(8);
            for (var i = 0; i < n; i++)
            {
                var (x0, y0) = points[i];
                var (x1, y1) = points[(i + 1) % n];
                if (Math.Abs(y1 - y0) < 1e-4f)
                    continue;
                if (y < Math.Min(y0, y1) || y >= Math.Max(y0, y1))
                    continue;
                var t = (y - y0) / (y1 - y0);
                crossings.Add(x0 + t * (x1 - x0));
            }

            crossings.Sort();
            var maskRow = _paintMask.DangerousGetPixelRowMemory(y).Span;
            var overlayRow = y * _overlayStride;
            for (var c = 0; c + 1 < crossings.Count; c += 2)
            {
                var xStart = (int)Math.Ceiling(crossings[c]);
                var xEnd = (int)Math.Floor(crossings[c + 1]);
                xStart = Math.Clamp(xStart, 0, _paintMask.Width - 1);
                xEnd = Math.Clamp(xEnd, 0, _paintMask.Width - 1);
                for (var x = xStart; x <= xEnd; x++)
                {
                    maskRow[x] = new L8(255);
                    var i = overlayRow + x * 4;
                    // Magenta-amber for lasso fill (distinct from paint brush yellow).
                    _overlayPixels[i] = 180;
                    _overlayPixels[i + 1] = 120;
                    _overlayPixels[i + 2] = 255;
                    _overlayPixels[i + 3] = 160;
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

    /// <summary>
    /// Builds a selection highlight with soft alpha from mask strength (SAM logits are soft).
    /// Display-only — does not change the L8 subject mask or in-game cutout hardness.
    /// </summary>
    private static BitmapSource ToMaskHighlightBitmap(Image<L8> mask, byte b, byte g, byte r, byte a)
    {
        var w = mask.Width;
        var h = mask.Height;
        var stride = w * 4;
        var pixels = new byte[stride * h];
        for (var y = 0; y < h; y++)
        {
            var row = mask.DangerousGetPixelRowMemory(y).Span;
            var dest = y * stride;
            for (var x = 0; x < w; x++)
            {
                var m = row[x].PackedValue;
                if (m == 0)
                {
                    dest += 4;
                    continue;
                }

                // Preserve soft boundary alphas instead of hard-thresholding at MaskKeepThreshold.
                var ha = (byte)((m * a + 127) / 255);
                pixels[dest++] = b;
                pixels[dest++] = g;
                pixels[dest++] = r;
                pixels[dest++] = ha;
            }
        }

        SoftenHighlightEdgePixels(pixels, w, h, stride);

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Light 1-px edge soften for hard (binary) masks so HighQuality scaling has soft alphas to blend.
    /// Purely cosmetic — source L8 masks are unchanged.
    /// </summary>
    private static void SoftenHighlightEdgePixels(byte[] pixels, int w, int h, int stride)
    {
        if (w < 2 || h < 2)
            return;

        var alpha = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            var row = y * stride;
            var dest = y * w;
            for (var x = 0; x < w; x++)
                alpha[dest + x] = pixels[row + x * 4 + 3];
        }

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var a0 = alpha[i];
                if (a0 == 0)
                    continue;

                var minN = a0;
                if (x > 0) minN = Math.Min(minN, alpha[i - 1]);
                if (x + 1 < w) minN = Math.Min(minN, alpha[i + 1]);
                if (y > 0) minN = Math.Min(minN, alpha[i - w]);
                if (y + 1 < h) minN = Math.Min(minN, alpha[i + w]);

                // Interior solid / already-soft pixels untouched; boundary against empty → half alpha.
                if (minN == 0 && a0 > 0)
                    pixels[y * stride + x * 4 + 3] = (byte)((a0 + 1) / 2);
            }
        }
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

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }
}
