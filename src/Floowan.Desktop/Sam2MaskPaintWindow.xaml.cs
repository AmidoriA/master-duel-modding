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
/// SAM selection editor: Click (default), Lasso, Paint — shared cyan working selection + Undo.
/// </summary>
public partial class Sam2MaskPaintWindow : Window
{
    private const int MaxUndoLevels = 20;

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

        ExistingSubjectOverlay.Source = ToMaskHighlightBitmap(
            existingSubjectMask, b: 70, g: 210, r: 40, a: 150);
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
            _workingClickMask, b: 230, g: 200, r: 40, a: 170);
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
                "Click object (default): cyan working selection. Left-click adds, right-click removes. Undo / Reset available.";
            StatusText.Text = CountOpaque(_workingClickMask) == 0
                ? "Click object: left-click to add, right-click to remove."
                : "Left-click adds, right-click removes (incl. prior session), Undo, or Apply.";
        }
        else if (IsLassoMode)
        {
            PaintHost.Cursor = Cursors.Cross;
            HelpText.Text =
                "Lasso: drag a freehand loop. On release the interior fills, SAM runs, and the result unions into cyan.";
            StatusText.Text = "Lasso: drag around a region, release to run SAM 2.";
        }
        else
        {
            PaintHost.Cursor = Cursors.None;
            HelpText.Text =
                "Paint: amber brush prompt. On stroke end SAM unions into cyan. Right-erase adjusts amber. Circle cursor = brush size.";
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
        StatusText.Text = "Paint prompt cleared (cyan working selection kept).";
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
                $"Cyan updated from {sourceLabel}. Continue, Undo, or Apply.";
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

    private void StampBrush(int cx, int cy, bool erase)
    {
        var r = _brushRadius;
        var r2 = r * r;
        var minX = Math.Max(cx - r, 0);
        var maxX = Math.Min(cx + r, _paintMask.Width - 1);
        var minY = Math.Max(cy - r, 0);
        var maxY = Math.Min(cy + r, _paintMask.Height - 1);

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

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
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

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }
}
