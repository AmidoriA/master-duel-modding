using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Floowan.Core.Assets;
using Floowan.Core.Data;
using Floowan.Core.Imaging;
using Floowan.Core.Models;
using Floowan.Core.Services;
using Microsoft.Win32;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace Floowan.Desktop;

public partial class CustomOverframeWindow : Window
{
    private readonly AutoOverFrameArtService _autoArt;
    private readonly Sam2PointCutoutService _sam2Cutout = new();
    private readonly OverFrameModService _overFrameService;
    private readonly CardRecord _card;
    private readonly string _gamePath;
    private readonly CardDatabase? _database;
    private readonly CardLinkMarkerLoader _linkMarkerLoader = new();
    private LinkMarkerMask? _linkMarkers;

    private Image<Rgba32>? _subjectSource;
    private Image<L8>? _subjectMask;
    private Image<Rgba32>? _backgroundSource;
    private string? _composedTempPath;
    private int _offsetX;
    private int _offsetY;
    private const float DefaultSubjectScale = 1.5f;
    private const float DefaultBackgroundScale = 1.0f;

    private float _subjectScale = DefaultSubjectScale;
    private float _backgroundScale = DefaultBackgroundScale;
    private int _backgroundOffsetX;
    private int _backgroundOffsetY;
    private bool _busy;
    private bool _samPointPickMode;
    private Image<Rgba32>? _samPickSource;
    private bool _dragging;
    private bool _scaleDragging;
    private bool _bgTransformDragging;
    private bool _updatingBgPanSliders;
    private bool _defaultBackgroundStarted;
    private int _dragVisualGeneration;
    private System.Windows.Point _dragStart;
    private int _dragStartOffsetX;
    private int _dragStartOffsetY;

    public string? ResultMessage { get; private set; }

    public CustomOverframeWindow(
        AutoOverFrameArtService autoArt,
        OverFrameModService overFrameService,
        CardRecord card,
        string gamePath,
        CardDatabase? database,
        CardFrameStyle initialFrameStyle,
        LinkMarkerMask? linkMarkers = null)
    {
        InitializeComponent();
        _autoArt = autoArt;
        _overFrameService = overFrameService;
        _card = card;
        _gamePath = gamePath;
        _database = database;
        _linkMarkers = linkMarkers;
        Title = $"Custom overframe art — {card.DisplayName}";
        SelectFrameStyle(initialFrameStyle);
        FromCurrentArtSam2Button.Visibility = Sam2PointCutoutService.IsFeatureEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewKeyDown += CustomOverframeWindow_PreviewKeyDown;
        // Do not sync-load CARD_Prop here — that freezes the dialog. Parent may have
        // pre-resolved markers; otherwise load async on first Link frame selection / Loaded.
        if (LinkArrowOverlay.NeedsArrowOverlay(initialFrameStyle) && _linkMarkers is null)
            Loaded += CustomOverframeWindow_LoadLinkMarkersOnOpen;
        ArtScaleSlider.Value = DefaultSubjectScale;
        _subjectScale = DefaultSubjectScale;
        BgScaleSlider.Value = DefaultBackgroundScale;
        _backgroundScale = DefaultBackgroundScale;
        ArtScaleSlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(ArtScaleSlider_DragStarted),
            handledEventsToo: true);
        ArtScaleSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(ArtScaleSlider_DragCompleted),
            handledEventsToo: true);
        BgScaleSlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(BgTransformSlider_DragStarted),
            handledEventsToo: true);
        BgScaleSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(BgTransformSlider_DragCompleted),
            handledEventsToo: true);
        BgPanHSlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(BgTransformSlider_DragStarted),
            handledEventsToo: true);
        BgPanHSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(BgTransformSlider_DragCompleted),
            handledEventsToo: true);
        BgPanVSlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler(BgTransformSlider_DragStarted),
            handledEventsToo: true);
        BgPanVSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(BgTransformSlider_DragCompleted),
            handledEventsToo: true);
        UpdateArtScaleLabel();
        UpdateBackgroundTransformLabels();
        SyncBackgroundPanSliderRanges();
        Loaded += CustomOverframeWindow_Loaded;
        Closed += (_, _) => Cleanup();
    }

    private async void CustomOverframeWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_defaultBackgroundStarted)
            return;

        _defaultBackgroundStarted = true;
        await LoadCardArtAsBackgroundAsync(
            "Loading card art as background…",
            readyStatus: "Background: current card art (Cover). Pick a subject…");
    }

    private void SelectFrameStyle(CardFrameStyle style)
    {
        for (var i = 0; i < FrameStyleBox.Items.Count; i++)
        {
            if (FrameStyleBox.Items[i] is ComboBoxItem item
                && item.Tag is string tag
                && Enum.TryParse<CardFrameStyle>(tag, out var parsed)
                && parsed == style)
            {
                FrameStyleBox.SelectedIndex = i;
                return;
            }
        }

        FrameStyleBox.SelectedIndex = 0; // Effect
    }

    private static readonly TimeSpan LinkMarkerLoadTimeout = TimeSpan.FromSeconds(30);

    private async void CustomOverframeWindow_LoadLinkMarkersOnOpen(object sender, RoutedEventArgs e)
    {
        Loaded -= CustomOverframeWindow_LoadLinkMarkersOnOpen;
        await RefreshLinkMarkersForFrameAsync(GetSelectedFrameStyle());
    }

    private CardFrameStyle GetSelectedFrameStyle()
    {
        if (FrameStyleBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && Enum.TryParse<CardFrameStyle>(tag, out var selected))
        {
            return selected;
        }

        return CardFrameStyle.Effect;
    }

    private async Task RefreshLinkMarkersForFrameAsync(CardFrameStyle frameStyle)
    {
        if (!LinkArrowOverlay.NeedsArrowOverlay(frameStyle))
        {
            _linkMarkers = null;
            return;
        }

        // Prefer catalog value — avoid LocalData scans while editing.
        if (_card.LinkMarkers is { } fromCard)
        {
            _linkMarkers = fromCard;
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(LinkMarkerLoadTimeout);
            var cardId = _card.Id;
            var gamePath = _gamePath;
            var loadTask = Task.Run(
                () =>
                {
                    if (_linkMarkerLoader.TryGetMarkers(gamePath, cardId, out var markers, cts.Token))
                        return (LinkMarkerMask?)markers;
                    return null;
                },
                CancellationToken.None);
            _linkMarkers = await loadTask.WaitAsync(cts.Token).ConfigureAwait(true);
        }
        catch
        {
            _linkMarkers = null;
        }
    }

    private float GetSubjectScale() =>
        OverFrameAutoArtComposer.ClampSubjectScale((float)ArtScaleSlider.Value);

    private float GetBackgroundScale() =>
        OverFrameAutoArtComposer.ClampBackgroundScale((float)BgScaleSlider.Value);

    private void UpdateArtScaleLabel()
    {
        ArtScaleValueText.Text = $"×{GetSubjectScale():0.00}";
    }

    private void UpdateBackgroundTransformLabels()
    {
        BgScaleValueText.Text = $"×{GetBackgroundScale():0.00}";
        BgPanHValueText.Text = ((int)Math.Round(BgPanHSlider.Value)).ToString();
        BgPanVValueText.Text = ((int)Math.Round(BgPanVSlider.Value)).ToString();
    }

    /// <summary>
    /// Sets H/V pan slider ranges from Cover×scale overflow vs the current frame hole,
    /// and clamps the current pan into the new limits.
    /// </summary>
    private void SyncBackgroundPanSliderRanges()
    {
        var artWindow = OverFrameAutoArtComposer.ResolveCustomBackgroundArtWindow(
            GetSelectedFrameStyle());
        var bgW = _backgroundSource?.Width ?? 1;
        var bgH = _backgroundSource?.Height ?? 1;
        var (maxPanX, maxPanY) = OverFrameAutoArtComposer.GetBackgroundPanLimits(
            bgW, bgH, artWindow, GetBackgroundScale());

        _updatingBgPanSliders = true;
        try
        {
            BgPanHSlider.Minimum = -maxPanX;
            BgPanHSlider.Maximum = maxPanX;
            BgPanVSlider.Minimum = -maxPanY;
            BgPanVSlider.Maximum = maxPanY;

            var panX = OverFrameAutoArtComposer.ClampBackgroundPan(
                (int)Math.Round(BgPanHSlider.Value), maxPanX);
            var panY = OverFrameAutoArtComposer.ClampBackgroundPan(
                (int)Math.Round(BgPanVSlider.Value), maxPanY);
            BgPanHSlider.Value = panX;
            BgPanVSlider.Value = panY;
            _backgroundOffsetX = panX;
            _backgroundOffsetY = panY;
            BgPanHSlider.IsEnabled = maxPanX > 0;
            BgPanVSlider.IsEnabled = maxPanY > 0;
        }
        finally
        {
            _updatingBgPanSliders = false;
        }

        UpdateBackgroundTransformLabels();
    }

    private void ResetBackgroundPlacement()
    {
        _backgroundScale = DefaultBackgroundScale;
        _backgroundOffsetX = 0;
        _backgroundOffsetY = 0;
        _updatingBgPanSliders = true;
        try
        {
            BgScaleSlider.Value = DefaultBackgroundScale;
            BgPanHSlider.Value = 0;
            BgPanVSlider.Value = 0;
        }
        finally
        {
            _updatingBgPanSliders = false;
        }

        SyncBackgroundPanSliderRanges();
    }

    private void ArtScaleSlider_DragStarted(object sender, DragStartedEventArgs e) =>
        _scaleDragging = true;

    private async void ArtScaleSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _scaleDragging = false;
        await ApplyArtScaleChangeAsync();
    }

    private async void ArtScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        UpdateArtScaleLabel();
        if (_scaleDragging)
            return;

        await ApplyArtScaleChangeAsync();
    }

    private void BgTransformSlider_DragStarted(object sender, DragStartedEventArgs e) =>
        _bgTransformDragging = true;

    private async void BgTransformSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _bgTransformDragging = false;
        await ApplyBackgroundTransformChangeAsync();
    }

    private async void BgScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _updatingBgPanSliders)
            return;

        SyncBackgroundPanSliderRanges();
        if (_bgTransformDragging)
            return;

        await ApplyBackgroundTransformChangeAsync();
    }

    private async void BgPanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _updatingBgPanSliders)
            return;

        UpdateBackgroundTransformLabels();
        if (_bgTransformDragging)
            return;

        await ApplyBackgroundTransformChangeAsync();
    }

    private async void MatchBackgroundToSubject_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _backgroundSource is null)
            return;

        var subjectScale = GetSubjectScale();
        _updatingBgPanSliders = true;
        try
        {
            BgScaleSlider.Value = subjectScale;
        }
        finally
        {
            _updatingBgPanSliders = false;
        }

        // Pan limits depend on the new shared Cover scale — sync ranges first, then
        // copy subject offset (clamped to overflow so the hole stays covered when ≥ ×1).
        // Do not write _backgroundOffset* here; ApplyBackgroundTransformChangeAsync owns that.
        SyncBackgroundPanSliderRanges();
        _updatingBgPanSliders = true;
        try
        {
            var panX = OverFrameAutoArtComposer.ClampBackgroundPan(
                _offsetX, (int)Math.Round(BgPanHSlider.Maximum));
            var panY = OverFrameAutoArtComposer.ClampBackgroundPan(
                _offsetY, (int)Math.Round(BgPanVSlider.Maximum));
            BgPanHSlider.Value = panX;
            BgPanVSlider.Value = panY;
        }
        finally
        {
            _updatingBgPanSliders = false;
        }

        UpdateBackgroundTransformLabels();
        await ApplyBackgroundTransformChangeAsync();
        if (!_busy)
        {
            StatusText.Text =
                $"Matched background to subject: scale ×{_backgroundScale:0.00}, " +
                $"pan {_backgroundOffsetX}, {_backgroundOffsetY}.";
        }
    }

    private async Task ApplyArtScaleChangeAsync()
    {
        var scale = GetSubjectScale();
        if (Math.Abs(scale - _subjectScale) < 0.0001f
            && _subjectSource is not null
            && _composedTempPath is not null)
        {
            return;
        }

        _subjectScale = scale;
        if (_subjectSource is null || _subjectMask is null || _busy)
            return;

        SetBusy(true);
        StatusText.Text = $"Recomposing at art scale ×{_subjectScale:0.00}…";
        try
        {
            await RecomposePreviewAsync();
            StatusText.Text =
                $"Preview at scale ×{_subjectScale:0.00}, offset {_offsetX}, {_offsetY}. Drag or Apply.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Compose failed: " + ex.Message;
            MessageBox.Show(
                this,
                "Could not recompose overframe:\n\n" + ex.Message,
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ApplyBackgroundTransformChangeAsync()
    {
        var scale = GetBackgroundScale();
        var panX = (int)Math.Round(BgPanHSlider.Value);
        var panY = (int)Math.Round(BgPanVSlider.Value);
        if (Math.Abs(scale - _backgroundScale) < 0.0001f
            && panX == _backgroundOffsetX
            && panY == _backgroundOffsetY
            && _backgroundSource is not null)
        {
            // Still refresh when nothing changed only if we already have a preview —
            // skip no-op recomposes.
            return;
        }

        _backgroundScale = scale;
        _backgroundOffsetX = panX;
        _backgroundOffsetY = panY;

        if (_backgroundSource is null || _busy)
            return;

        SetBusy(true);
        StatusText.Text =
            $"Recomposing background ×{_backgroundScale:0.00}, pan {_backgroundOffsetX}, {_backgroundOffsetY}…";
        try
        {
            if (_subjectSource is not null && _subjectMask is not null)
            {
                await RecomposePreviewAsync();
                StatusText.Text =
                    $"Background ×{_backgroundScale:0.00}, pan {_backgroundOffsetX}, {_backgroundOffsetY}. " +
                    $"Subject scale ×{_subjectScale:0.00}, offset {_offsetX}, {_offsetY}.";
            }
            else
            {
                await ShowBackgroundOnlyPreviewAsync();
                StatusText.Text =
                    $"Background ×{_backgroundScale:0.00}, pan {_backgroundOffsetX}, {_backgroundOffsetY}. Pick a subject…";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Compose failed: " + ex.Message;
            MessageBox.Show(
                this,
                "Could not recompose overframe:\n\n" + ex.Message,
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void PickBackground_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var dlg = new OpenFileDialog
        {
            Title = "Select Cover background for custom overframe (art hole only)",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp|PNG|*.png|JPEG|*.jpg;*.jpeg|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var validation = ImagePreparation.Validate(
            dlg.FileName, OverFrameConstants.Width, OverFrameConstants.Height);
        if (!validation.IsValid)
        {
            MessageBox.Show(
                this,
                validation.Error ?? "Invalid image.",
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var sizeNote = CardArtTextureSizes.Describe(validation.Width, validation.Height);

        SetBusy(true);
        StatusText.Text = $"Loading background {Path.GetFileName(dlg.FileName)} ({sizeNote}, Cover)…";
        try
        {
            DisposeBackground();
            _backgroundSource = await Task.Run(() => ImageSharpImage.Load<Rgba32>(dlg.FileName));
            ResetBackgroundPlacement();
            await RefreshPreviewAfterBackgroundChangeAsync(
                subjectReadyStatus:
                    $"Background set ({sizeNote}). Preview updated — drag Card Art or Apply.",
                backgroundOnlyStatus:
                    $"Background ready ({sizeNote}). Pick a subject…");
        }
        catch (Exception ex)
        {
            DisposeBackground();
            StatusText.Text = "Background failed: " + ex.Message;
            MessageBox.Show(
                this,
                "Could not load background:\n\n" + ex.Message,
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UseCardArtBackground_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        await LoadCardArtAsBackgroundAsync(
            "Loading card art as background…",
            readyStatus: null);
    }

    /// <summary>
    /// Extracts live card art and installs it as the Cover background in the art hole.
    /// </summary>
    private async Task LoadCardArtAsBackgroundAsync(string loadingStatus, string? readyStatus)
    {
        SetBusy(true);
        string? liveTemp = null;
        try
        {
            StatusText.Text = loadingStatus;
            liveTemp = Path.Combine(
                Path.GetTempPath(),
                $"floowan-custom-of-bg-{Guid.NewGuid():N}.png");
            var gamePath = _gamePath;
            var card = _card;
            var outputPath = liveTemp;
            await Task.Run(() => _overFrameService.ExtractCardArt(gamePath, card, outputPath));

            DisposeBackground();
            _backgroundSource = await Task.Run(() => ImageSharpImage.Load<Rgba32>(liveTemp));
            ResetBackgroundPlacement();

            var subjectStatus = readyStatus
                ?? "Background: current card art (Cover). Preview updated — drag Card Art or Apply.";
            var backgroundOnlyStatus = readyStatus
                ?? "Background: current card art (Cover). Pick a subject…";
            await RefreshPreviewAfterBackgroundChangeAsync(subjectStatus, backgroundOnlyStatus);
        }
        catch (Exception ex)
        {
            DisposeBackground();
            PreviewImage.Source = null;
            PreviewHintText.Text = "Background failed";
            PreviewHintText.Visibility = Visibility.Visible;
            StatusText.Text = "Background failed: " + ex.Message;
            MessageBox.Show(
                this,
                "Could not load card art as background:\n\n" + ex.Message,
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (liveTemp is not null && File.Exists(liveTemp))
            {
                try { File.Delete(liveTemp); } catch { /* ignore */ }
            }

            SetBusy(false);
        }
    }

    private async Task RefreshPreviewAfterBackgroundChangeAsync(
        string subjectReadyStatus,
        string backgroundOnlyStatus)
    {
        if (_subjectSource is not null && _subjectMask is not null)
        {
            await RecomposePreviewAsync();
            StatusText.Text = subjectReadyStatus;
            PreviewHintText.Visibility = Visibility.Collapsed;
            return;
        }

        await ShowBackgroundOnlyPreviewAsync();
        StatusText.Text = backgroundOnlyStatus;
    }

    private async Task ShowBackgroundOnlyPreviewAsync()
    {
        var frameStyle = GetSelectedFrameStyle();
        var background = _backgroundSource;
        var backgroundScale = _backgroundScale;
        var backgroundOffsetX = _backgroundOffsetX;
        var backgroundOffsetY = _backgroundOffsetY;
        var linkMarkers = _linkMarkers;
        var bmp = await Task.Run(() =>
        {
            using var preview = OverFrameAutoArtComposer.ComposeCustomBackgroundOnly(
                frameStyle,
                background: background,
                backgroundScale: backgroundScale,
                backgroundOffsetX: backgroundOffsetX,
                backgroundOffsetY: backgroundOffsetY);
            AutoOverFrameArtService.ApplyLinkArrowsIfNeeded(preview, frameStyle, linkMarkers);
            return ToPreviewBitmap(preview);
        });

        PreviewImage.Source = bmp;
        ClearSubjectOverlay();
        PreviewHintText.Visibility = Visibility.Collapsed;
        CleanupComposedTemp();
    }

    private async void PickImage_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var dlg = new OpenFileDialog
        {
            Title = "Select subject image with alpha for custom overframe",
            Filter = "Images|*.png;*.webp;*.bmp|PNG (alpha)|*.png|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var validation = ImagePreparation.Validate(
            dlg.FileName, OverFrameConstants.Width, OverFrameConstants.Height);
        if (!validation.IsValid)
        {
            MessageBox.Show(
                this,
                validation.Error ?? "Invalid image.",
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var sizeNote = CardArtTextureSizes.Describe(validation.Width, validation.Height);
        await PrepareFromImageAsync(dlg.FileName, sizeNote);
    }

    private async void FromCurrentArtRembg_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        await PrepareSubjectFromLiveArtRembgAsync();
    }

    private void CustomOverframeWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_samPointPickMode)
            return;

        CancelSamPointPick();
        StatusText.Text = "SAM 2 point pick cancelled.";
        e.Handled = true;
    }

    private async void FromCurrentArtSam2_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !Sam2PointCutoutService.IsFeatureEnabled)
            return;

        await BeginSamPointPickAsync();
    }

    /// <summary>
    /// Extracts live card art into the preview and waits for a click that becomes the
    /// SAM 2 positive point prompt.
    /// </summary>
    private async Task BeginSamPointPickAsync()
    {
        SetBusy(true);
        string? liveTemp = null;
        try
        {
            CancelSamPointPick(clearStatus: false);
            liveTemp = Path.Combine(
                Path.GetTempPath(),
                $"floowan-custom-of-sam2-{Guid.NewGuid():N}.png");
            StatusText.Text = "Extracting live card art for SAM 2…";
            var gamePath = _gamePath;
            var card = _card;
            var outputPath = liveTemp;
            await Task.Run(() => _overFrameService.ExtractCardArt(gamePath, card, outputPath));

            var progress = new Progress<string>(msg => StatusText.Text = msg);
            await _sam2Cutout.EnsureModelsAsync(progress);

            using var loaded = ImageSharpImage.Load<Rgba32>(liveTemp);
            var clean = OverFrameAutoArtComposer.RequireCleanIllustrationSource(loaded);
            _samPickSource?.Dispose();
            _samPickSource = clean.Clone();
            if (!ReferenceEquals(loaded, clean))
                clean.Dispose();

            ClearSubjectOverlay();
            PreviewImage.Source = ToPreviewBitmap(_samPickSource);
            PreviewImage.Cursor = Cursors.Cross;
            PreviewHintText.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = false;
            _samPointPickMode = true;
            StatusText.Text =
                "SAM 2: click the subject on the preview (e.g. dragon, not rider). Esc cancels.";
        }
        catch (Exception ex)
        {
            CancelSamPointPick(clearStatus: false);
            StatusText.Text = "SAM 2 prepare failed: " + ex.Message;
            MessageBox.Show(
                this,
                "Could not prepare SAM 2 point cutout:\n\n" + ex.Message,
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (liveTemp is not null && File.Exists(liveTemp))
            {
                try { File.Delete(liveTemp); } catch { /* ignore */ }
            }

            SetBusy(false);
            if (_samPointPickMode)
                PreviewImage.Cursor = Cursors.Cross;
        }
    }

    private void CancelSamPointPick(bool clearStatus = true)
    {
        _samPointPickMode = false;
        _samPickSource?.Dispose();
        _samPickSource = null;
        PreviewImage.Cursor = Cursors.SizeAll;
        if (clearStatus && StatusText.Text.StartsWith("SAM 2:", StringComparison.Ordinal))
            StatusText.Text = "Ready.";
    }

    private async Task RunSam2AtPreviewPointAsync(System.Windows.Point hostPos)
    {
        if (_samPickSource is null || _busy)
            return;

        if (!Sam2PointCutoutService.TryMapPreviewClickToImage(
                hostPos.X,
                hostPos.Y,
                PreviewHost.ActualWidth,
                PreviewHost.ActualHeight,
                _samPickSource.Width,
                _samPickSource.Height,
                out var imageX,
                out var imageY))
        {
            StatusText.Text = "SAM 2: click inside the card art.";
            return;
        }

        SetBusy(true);
        StatusText.Text = $"SAM 2: segmenting at ({imageX:0},{imageY:0})…";
        string? tempPath = null;
        try
        {
            tempPath = Path.Combine(
                Path.GetTempPath(),
                $"floowan-sam2-src-{Guid.NewGuid():N}.png");
            _samPickSource.Save(tempPath, new SixLabors.ImageSharp.Formats.Png.PngEncoder());

            var progress = new Progress<string>(msg => StatusText.Text = msg);
            var prepared = await _sam2Cutout.PrepareSubjectWithPointAsync(
                tempPath,
                imageX,
                imageY,
                progress);

            CancelSamPointPick(clearStatus: false);
            ResetSubjectPlacement();
            _subjectSource = prepared.Source;
            _subjectMask = prepared.Mask;

            await RecomposePreviewAsync();
            StatusText.Text =
                $"Subject from SAM 2 point ({imageX:0},{imageY:0}). Drag or scale the art, then Apply.";
            PreviewHintText.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            await FailSubjectPrepareAsync(ex);
            if (_samPickSource is not null)
            {
                _samPointPickMode = true;
                PreviewImage.Source = ToPreviewBitmap(_samPickSource);
                PreviewImage.Cursor = Cursors.Cross;
                StatusText.Text =
                    "SAM 2 failed — click another point, or Esc to cancel. " + ex.Message;
            }
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }

            SetBusy(false);
        }
    }


    private async Task PrepareFromImageAsync(string imagePath, string sizeNote)
    {
        SetBusy(true);
        StatusText.Text = $"Loading {Path.GetFileName(imagePath)} ({sizeNote})…";
        try
        {
            ResetSubjectPlacement();

            var progress = new Progress<string>(msg => StatusText.Text = msg);
            var prepared = await Task.Run(
                () => AutoOverFrameArtService.LoadSubjectFromAlpha(imagePath, progress));
            _subjectSource = prepared.Source;
            _subjectMask = prepared.Mask;

            await RecomposePreviewAsync();
            StatusText.Text =
                $"Subject ready ({sizeNote}). Drag or scale the art, then Apply.";
            PreviewHintText.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            await FailSubjectPrepareAsync(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Extracts live card art, runs rembg, and installs the cutout as the Card Art
    /// subject layer (same preview path as Select subject…).
    /// </summary>
    private async Task PrepareSubjectFromLiveArtRembgAsync()
    {
        SetBusy(true);
        string? liveTemp = null;
        try
        {
            ResetSubjectPlacement();
            liveTemp = Path.Combine(
                Path.GetTempPath(),
                $"floowan-custom-of-live-{Guid.NewGuid():N}.png");
            StatusText.Text = "Extracting live card art…";
            var gamePath = _gamePath;
            var card = _card;
            var outputPath = liveTemp;
            await Task.Run(() => _overFrameService.ExtractCardArt(gamePath, card, outputPath));

            var progress = new Progress<string>(msg => StatusText.Text = msg);
            var prepared = await _autoArt.PrepareSubjectWithRembgAsync(liveTemp, progress);
            _subjectSource = prepared.Source;
            _subjectMask = prepared.Mask;

            await RecomposePreviewAsync();
            StatusText.Text =
                "Subject from live art (rembg). Drag or scale the art, then Apply.";
            PreviewHintText.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            await FailSubjectPrepareAsync(ex);
        }
        finally
        {
            if (liveTemp is not null && File.Exists(liveTemp))
            {
                try { File.Delete(liveTemp); } catch { /* ignore */ }
            }

            SetBusy(false);
        }
    }

    private void ResetSubjectPlacement()
    {
        DisposeSubject();
        _offsetX = 0;
        _offsetY = 0;
        _subjectScale = DefaultSubjectScale;
        ArtScaleSlider.Value = DefaultSubjectScale;
        UpdateArtScaleLabel();
        ClearSubjectOverlay();
    }

    private async Task FailSubjectPrepareAsync(Exception ex)
    {
        DisposeSubject();
        ClearSubjectOverlay();
        ApplyButton.IsEnabled = false;
        StatusText.Text = "Failed: " + ex.Message;
        MessageBox.Show(
            this,
            "Could not prepare custom overframe art:\n\n" + ex.Message,
            "Custom overframe art",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        if (_backgroundSource is not null)
        {
            try
            {
                await ShowBackgroundOnlyPreviewAsync();
                StatusText.Text =
                    "Subject failed. Background still ready — pick another subject…";
            }
            catch
            {
                PreviewImage.Source = null;
                PreviewHintText.Text = "Select a subject to preview";
                PreviewHintText.Visibility = Visibility.Visible;
            }
        }
        else
        {
            PreviewImage.Source = null;
            PreviewHintText.Text = "Select a subject to preview";
            PreviewHintText.Visibility = Visibility.Visible;
        }
    }

    private async void FrameStyleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _busy)
            return;

        await RefreshLinkMarkersForFrameAsync(GetSelectedFrameStyle());

        if (_subjectSource is null || _subjectMask is null)
        {
            if (_backgroundSource is null)
                return;

            SetBusy(true);
            try
            {
                SyncBackgroundPanSliderRanges();
                await ShowBackgroundOnlyPreviewAsync();
                StatusText.Text =
                    $"Background preview ({GetSelectedFrameStyle()}). Pick a subject…";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Compose failed: " + ex.Message;
                MessageBox.Show(
                    this,
                    "Could not recompose overframe:\n\n" + ex.Message,
                    "Custom overframe art",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }

            return;
        }

        SetBusy(true);
        try
        {
            SyncBackgroundPanSliderRanges();
            await RecomposePreviewAsync();
            StatusText.Text =
                $"Preview updated ({GetSelectedFrameStyle()}). Drag or scale the art, then Apply.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Compose failed: " + ex.Message;
            MessageBox.Show(
                this,
                "Could not recompose overframe:\n\n" + ex.Message,
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RecomposePreviewAsync()
    {
        if (_subjectSource is null || _subjectMask is null)
            return;

        var frameStyle = GetSelectedFrameStyle();
        var offsetX = _offsetX;
        var offsetY = _offsetY;
        var subjectScale = _subjectScale;
        var source = _subjectSource;
        var mask = _subjectMask;
        var background = _backgroundSource;
        var backgroundScale = _backgroundScale;
        var backgroundOffsetX = _backgroundOffsetX;
        var backgroundOffsetY = _backgroundOffsetY;

        CleanupComposedTemp();
        _composedTempPath = Path.Combine(
            Path.GetTempPath(),
            $"floowan-custom-of-{Guid.NewGuid():N}.png");

        var outputPath = _composedTempPath;
        var linkMarkers = _linkMarkers;
        await Task.Run(() =>
            AutoOverFrameArtService.ComposePreparedSubject(
                source,
                mask,
                outputPath,
                frameStyle,
                offsetX,
                offsetY,
                subjectScale,
                OverFrameComposeMode.CustomArtOnly,
                background,
                backgroundScale,
                backgroundOffsetX,
                backgroundOffsetY,
                linkMarkers));

        PreviewImage.Source = LoadOfComposePreview(outputPath);
        ClearSubjectOverlay();
    }

    private async Task BeginSubjectDragVisualAsync(int generation)
    {
        if (_subjectSource is null || _subjectMask is null)
            return;

        var frameStyle = GetSelectedFrameStyle();
        var offsetX = _offsetX;
        var offsetY = _offsetY;
        var subjectScale = _subjectScale;
        var source = _subjectSource;
        var mask = _subjectMask;
        var background = _backgroundSource;
        var backgroundScale = _backgroundScale;
        var backgroundOffsetX = _backgroundOffsetX;
        var backgroundOffsetY = _backgroundOffsetY;
        var linkMarkers = _linkMarkers;

        var (baseBmp, subjectBmp) = await Task.Run(() =>
        {
            using var baseLayer = OverFrameAutoArtComposer.ComposeBaseWithoutSubject(
                source,
                mask,
                frameStyle,
                subjectScale: subjectScale,
                composeMode: OverFrameComposeMode.CustomArtOnly,
                background: background,
                backgroundScale: backgroundScale,
                backgroundOffsetX: backgroundOffsetX,
                backgroundOffsetY: backgroundOffsetY);
            // Arrows sit above chrome/background but under the dragged subject layer.
            AutoOverFrameArtService.ApplyLinkArrowsIfNeeded(baseLayer, frameStyle, linkMarkers);
            using var subjectLayer = OverFrameAutoArtComposer.RenderSubjectDragLayer(
                source,
                mask,
                frameStyle,
                subjectOffsetX: offsetX,
                subjectOffsetY: offsetY,
                subjectScale: subjectScale,
                composeMode: OverFrameComposeMode.CustomArtOnly);
            return (ToPreviewBitmap(baseLayer), ToPreviewBitmap(subjectLayer));
        });

        if (generation != _dragVisualGeneration || !_dragging)
            return;

        PreviewImage.Source = baseBmp;
        SubjectOverlay.Source = subjectBmp;
        ResetSubjectDragTransform();
        SubjectOverlay.Visibility = Visibility.Visible;
    }

    private async void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy || PreviewImage.Source is null)
            return;

        if (_samPointPickMode)
        {
            e.Handled = true;
            await RunSam2AtPreviewPointAsync(e.GetPosition(PreviewHost));
            return;
        }

        if (_subjectSource is null)
            return;

        _dragging = true;
        _dragStart = e.GetPosition(PreviewHost);
        _dragStartOffsetX = _offsetX;
        _dragStartOffsetY = _offsetY;
        PreviewHost.CaptureMouse();
        e.Handled = true;

        // Swap to base + subject overlay so live drag moves subject only.
        var generation = ++_dragVisualGeneration;
        _ = BeginSubjectDragVisualAsync(generation);
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        var pos = e.GetPosition(PreviewHost);
        var scale = GetPreviewCanvasScale();
        if (scale <= 0)
            return;

        var dx = (pos.X - _dragStart.X) / scale;
        var dy = (pos.Y - _dragStart.Y) / scale;
        SubjectDragTransform.X = pos.X - _dragStart.X;
        SubjectDragTransform.Y = pos.Y - _dragStart.Y;
        StatusText.Text =
            $"Offset {_dragStartOffsetX + (int)Math.Round(dx)}, {_dragStartOffsetY + (int)Math.Round(dy)} (release to recompose)";
    }

    private async void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;

        await FinishDragAsync(e.GetPosition(PreviewHost));
    }

    private async void Preview_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton == MouseButtonState.Pressed)
            return;

        await FinishDragAsync(e.GetPosition(PreviewHost));
    }

    private async void Preview_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        await FinishDragAsync(e.GetPosition(PreviewHost));
    }

    private async Task FinishDragAsync(System.Windows.Point pos)
    {
        if (!_dragging)
            return;

        _dragging = false;
        _dragVisualGeneration++;
        // Drop live overlay immediately so a translated subject cannot cover lore cream
        // while the full recompose (same lore paint path as initial compose) runs.
        ClearSubjectOverlay();
        PreviewHost.ReleaseMouseCapture();

        var scale = GetPreviewCanvasScale();
        if (scale > 0)
        {
            var dx = (pos.X - _dragStart.X) / scale;
            var dy = (pos.Y - _dragStart.Y) / scale;
            _offsetX = _dragStartOffsetX + (int)Math.Round(dx);
            _offsetY = _dragStartOffsetY + (int)Math.Round(dy);
        }

        if (_busy || _subjectSource is null || _subjectMask is null)
            return;

        SetBusy(true);
        StatusText.Text = $"Recomposing at offset {_offsetX}, {_offsetY}…";
        try
        {
            await RecomposePreviewAsync();
            StatusText.Text =
                $"Preview at scale ×{_subjectScale:0.00}, offset {_offsetX}, {_offsetY}. Drag or Apply.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Compose failed: " + ex.Message;
            MessageBox.Show(
                this,
                "Could not recompose overframe:\n\n" + ex.Message,
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Maps preview control pixels to 704×1024 canvas pixels (Uniform stretch).
    /// </summary>
    private double GetPreviewCanvasScale()
    {
        // Prefer the fixed card preview; fall back to overlay while dragging.
        BitmapSource? bmp = PreviewImage.Source as BitmapSource
            ?? SubjectOverlay.Source as BitmapSource;
        if (bmp is null)
            return 0;

        var availableW = PreviewHost.ActualWidth;
        var availableH = PreviewHost.ActualHeight;
        if (availableW <= 0 || availableH <= 0)
            return 0;

        return Math.Min(availableW / bmp.PixelWidth, availableH / bmp.PixelHeight);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || string.IsNullOrWhiteSpace(_composedTempPath) || !File.Exists(_composedTempPath))
        {
            MessageBox.Show(
                this,
                "Compose a preview first (select an image).",
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        StatusText.Text = "Applying over-frame (texture + gate)…";
        try
        {
            var imagePath = _composedTempPath;
            var result = await Task.Run(() =>
                _overFrameService.ApplyOverFrame(
                    _gamePath,
                    _card,
                    imagePath,
                    createBackup: true,
                    _database));

            ResultMessage = result.Message;
            if (!result.Success)
            {
                StatusText.Text = result.Message;
                MessageBox.Show(
                    this,
                    result.Message,
                    "Custom overframe art",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Apply failed: " + ex.Message;
            MessageBox.Show(
                this,
                ex.Message,
                "Custom overframe art",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PickBackgroundButton.IsEnabled = !busy;
        UseCardArtBackgroundButton.IsEnabled = !busy;
        MatchBackgroundToSubjectButton.IsEnabled = !busy && _backgroundSource is not null;
        PickImageButton.IsEnabled = !busy;
        FromCurrentArtRembgButton.IsEnabled = !busy;
        FromCurrentArtSam2Button.IsEnabled = !busy && Sam2PointCutoutService.IsFeatureEnabled;
        FrameStyleBox.IsEnabled = !busy;
        ArtScaleSlider.IsEnabled = !busy;
        BgScaleSlider.IsEnabled = !busy;
        if (!busy)
            SyncBackgroundPanSliderRanges();
        else
        {
            BgPanHSlider.IsEnabled = false;
            BgPanVSlider.IsEnabled = false;
        }
        ApplyButton.IsEnabled = !busy && _subjectSource is not null && _composedTempPath is not null;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    private void ResetSubjectDragTransform()
    {
        SubjectDragTransform.X = 0;
        SubjectDragTransform.Y = 0;
    }

    private void ClearSubjectOverlay()
    {
        SubjectOverlay.Visibility = Visibility.Collapsed;
        SubjectOverlay.Source = null;
        ResetSubjectDragTransform();
    }

    private void DisposeSubject()
    {
        _subjectSource?.Dispose();
        _subjectMask?.Dispose();
        _subjectSource = null;
        _subjectMask = null;
    }

    private void DisposeBackground()
    {
        _backgroundSource?.Dispose();
        _backgroundSource = null;
    }

    private void CleanupComposedTemp()
    {
        if (_composedTempPath is not null && File.Exists(_composedTempPath))
        {
            try { File.Delete(_composedTempPath); } catch { /* ignore */ }
        }

        _composedTempPath = null;
    }

    private void Cleanup()
    {
        CancelSamPointPick(clearStatus: false);
        DisposeSubject();
        DisposeBackground();
        CleanupComposedTemp();
        _sam2Cutout.Dispose();
    }

    private static BitmapImage LoadOfComposePreview(string path)
    {
        using var image = ImageSharpImage.Load<Rgba32>(path);
        return ToPreviewBitmap(image);
    }

    private static BitmapImage ToPreviewBitmap(Image<Rgba32> image)
    {
        using var flat = OverFrameAutoArtComposer.FlattenFoilMaskForPreview(image);
        using var ms = new MemoryStream();
        flat.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        ms.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
