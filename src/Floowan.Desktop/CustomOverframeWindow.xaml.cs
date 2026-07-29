using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Floowan.Core.Assets;
using Floowan.Core.Backup;
using Floowan.Core.Data;
using Floowan.Core.Imaging;
using Floowan.Core.Models;
using Floowan.Core.Services;
using Microsoft.Win32;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
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
    /// <summary>True when Cover background is live card art (default / "Use card art").</summary>
    private bool _backgroundIsCardArt;
    /// <summary>
    /// True when the Card Art subject came from this card's live art (rembg Auto detect
    /// or SAM Add more selection). Used with <see cref="_backgroundIsCardArt"/> to
    /// auto-match background scale/pan on drag/scale. False for Pick manually… PNGs.
    /// </summary>
    private bool _subjectIsFromCardArt;
    private const float DefaultSubjectScale = 1.5f;
    private const float DefaultBackgroundScale = 1.0f;

    private float _subjectScale = DefaultSubjectScale;
    private float _backgroundScale = DefaultBackgroundScale;
    private int _backgroundOffsetX;
    private int _backgroundOffsetY;
    private bool _busy;
    private bool _dragging;
    private bool _scaleDragging;
    private bool _bgTransformDragging;
    private bool _updatingBgPanSliders;
    /// <summary>True while restoring a saved Custom OF stage (suppress radio/slider side effects).</summary>
    private bool _loadingStage;
    /// <summary>Art scale changed while busy — flush once idle.</summary>
    private bool _pendingArtScaleApply;
    /// <summary>Subject offset changed while busy — flush once idle.</summary>
    private bool _pendingSubjectRecompose;
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
        AdvancedSamExpander.Visibility = Sam2PointCutoutService.IsFeatureEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        if (await TryLoadSavedStageAsync())
            return;

        var alreadyOf = IsCardAlreadyOverframe();
        await LoadCardArtAsBackgroundAsync(
            alreadyOf
                ? "Loading pre-OF card art as background…"
                : "Loading card art as background…",
            readyStatus: alreadyOf
                ? "No saved Custom OF stage — Cover uses pre-OF art. Current OF preview shown until you pick a subject."
                : "Background: current card art (Cover). Pick a subject…");

        if (alreadyOf && _subjectSource is null)
            await TryShowCurrentOverFramePreviewAsync();
    }

    /// <summary>
    /// Restores subject/background layers and transforms from the last Custom OF Apply.
    /// </summary>
    private async Task<bool> TryLoadSavedStageAsync()
    {
        _loadingStage = true;
        SetBusy(true);
        StatusText.Text = "Loading saved Custom OF stage…";
        try
        {
            var cardName = _card.Name;
            var loaded = await Task.Run(() =>
            {
                if (!_overFrameService.Backups.TryLoadCustomOverframeStage(
                        cardName,
                        out var state,
                        out var subject,
                        out var mask,
                        out var background))
                {
                    return (Ok: false, State: (CustomOverframeStageState?)null,
                        Subject: (Image<Rgba32>?)null, Mask: (Image<L8>?)null,
                        Background: (Image<Rgba32>?)null);
                }

                return (Ok: true, State: (CustomOverframeStageState?)state,
                    Subject: subject, Mask: mask, Background: background);
            });

            if (!loaded.Ok || loaded.State is null)
            {
                loaded.Subject?.Dispose();
                loaded.Mask?.Dispose();
                loaded.Background?.Dispose();
                return false;
            }

            DisposeSubject();
            DisposeBackground();
            _subjectSource = loaded.Subject;
            _subjectMask = loaded.Mask;
            _backgroundSource = loaded.Background;
            _backgroundIsCardArt = loaded.State.BackgroundIsCardArt;
            _subjectIsFromCardArt = loaded.State.SubjectIsFromCardArt;

            if (Enum.TryParse<CardFrameStyle>(loaded.State.FrameStyle, ignoreCase: true, out var frameStyle))
                SelectFrameStyle(frameStyle);

            _subjectScale = OverFrameAutoArtComposer.ClampSubjectScale(loaded.State.SubjectScale);
            _offsetX = loaded.State.SubjectOffsetX;
            _offsetY = loaded.State.SubjectOffsetY;

            _backgroundScale = OverFrameAutoArtComposer.ClampBackgroundScale(loaded.State.BackgroundScale);
            _backgroundOffsetX = loaded.State.BackgroundOffsetX;
            _backgroundOffsetY = loaded.State.BackgroundOffsetY;

            // Write all transform controls under a suppress flag so ValueChanged handlers
            // do not re-enter Apply/Sync while offsets are still being restored.
            _updatingBgPanSliders = true;
            try
            {
                ArtScaleSlider.Value = _subjectScale;
                BgScaleSlider.Value = _backgroundScale;
            }
            finally
            {
                _updatingBgPanSliders = false;
            }

            UpdateArtScaleLabel();
            SyncBackgroundPanSliderRanges();

            _updatingBgPanSliders = true;
            try
            {
                // Re-apply pans from stage after Sync clamped against the new scale limits.
                var maxX = (int)Math.Round(BgPanHSlider.Maximum);
                var maxY = (int)Math.Round(BgPanVSlider.Maximum);
                _backgroundOffsetX = OverFrameAutoArtComposer.ClampBackgroundPan(
                    loaded.State.BackgroundOffsetX, maxX);
                _backgroundOffsetY = OverFrameAutoArtComposer.ClampBackgroundPan(
                    loaded.State.BackgroundOffsetY, maxY);
                BgPanHSlider.Value = _backgroundOffsetX;
                BgPanVSlider.Value = _backgroundOffsetY;
            }
            finally
            {
                _updatingBgPanSliders = false;
            }

            UpdateBackgroundTransformLabels();

            if (_subjectIsFromCardArt)
                SubjectAutoRadio.IsChecked = true;
            else if (_subjectSource is not null)
                SubjectManualRadio.IsChecked = true;

            if (_subjectSource is not null && _subjectMask is not null)
            {
                await RecomposePreviewAsync();
                ApplyButton.IsEnabled = true;
                PreviewHintText.Visibility = Visibility.Collapsed;
                StatusText.Text =
                    $"Loaded saved Custom OF stage (scale ×{_subjectScale:0.00}, offset {_offsetX}, {_offsetY}). Drag or Apply.";
            }
            else if (_backgroundSource is not null)
            {
                await ShowBackgroundOnlyPreviewAsync();
                StatusText.Text = "Loaded saved Custom OF background. Pick a subject…";
            }
            else
            {
                return false;
            }

            if (LinkArrowOverlay.NeedsArrowOverlay(GetSelectedFrameStyle()) && _linkMarkers is null)
                await RefreshLinkMarkersForFrameAsync(GetSelectedFrameStyle());

            return true;
        }
        catch (Exception ex)
        {
            DisposeSubject();
            DisposeBackground();
            StatusText.Text = "Could not load saved stage: " + ex.Message;
            return false;
        }
        finally
        {
            // Clear any sticky drag/scale flags from mid-load enable/disable races.
            _dragging = false;
            _scaleDragging = false;
            _bgTransformDragging = false;
            _updatingBgPanSliders = false;
            _pendingArtScaleApply = false;
            _pendingSubjectRecompose = false;
            SetBusy(false);
            _loadingStage = false;
        }
    }

    private bool IsCardAlreadyOverframe()
    {
        if (_card.IsOverframe)
            return true;
        try
        {
            var info = _overFrameService.GetTextureInfo(_gamePath, _card);
            return OverFrameAutoArtComposer.IsOverFrameTextureSize(info.Width, info.Height);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Shows the live/applied flat OF canvas as a read-only preview until the user
    /// rebuilds editable layers (no saved Custom OF stage).
    /// </summary>
    private async Task TryShowCurrentOverFramePreviewAsync()
    {
        string? temp = null;
        try
        {
            temp = Path.Combine(
                Path.GetTempPath(),
                $"floowan-custom-of-current-{Guid.NewGuid():N}.png");
            var gamePath = _gamePath;
            var card = _card;
            var outputPath = temp;
            var ok = await Task.Run(() =>
                _overFrameService.TryExportCurrentOverFrameCanvas(gamePath, card, outputPath));
            if (!ok || !File.Exists(temp))
                return;

            PreviewImage.Source = LoadOfComposePreview(temp);
            PreviewHintText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            /* best effort — background-only preview remains */
        }
        finally
        {
            if (temp is not null && File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Exports illustration for Cover / rembg / SAM, preferring clean pre-OF sources when
    /// the live texture is already over-framed.
    /// </summary>
    private Task ExportIllustrationForEditingAsync(string outputPngPath) =>
        Task.Run(() =>
            _overFrameService.ResolveCustomOfIllustrationSource(_gamePath, _card, outputPngPath));


    private void SelectFrameStyle(CardFrameStyle style)
    {
        // Dropdown lists solid type names only; OF compose maps to OfGradient*.
        var solid = CardFrameTemplates.GetSolidBaseStyle(style);
        for (var i = 0; i < FrameStyleBox.Items.Count; i++)
        {
            if (FrameStyleBox.Items[i] is ComboBoxItem item
                && item.Tag is string tag
                && Enum.TryParse<CardFrameStyle>(tag, out var parsed)
                && parsed == solid)
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

    /// <summary>
    /// Frame style for OF compose — always the OfGradient* equivalent of the dropdown selection.
    /// </summary>
    private CardFrameStyle GetSelectedFrameStyle()
    {
        if (FrameStyleBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && Enum.TryParse<CardFrameStyle>(tag, out var selected))
        {
            return CardFrameTemplates.ToOfGradientStyle(selected);
        }

        return CardFrameTemplates.ToOfGradientStyle(CardFrameStyle.Effect);
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
        if (!IsLoaded || _loadingStage || _updatingBgPanSliders)
            return;

        UpdateArtScaleLabel();
        // Recover sticky thumb-drag when SetBusy disabled the slider mid-gesture
        // (DragCompleted may never fire). Without this, Apply never runs again.
        if (_scaleDragging && Mouse.LeftButton != MouseButtonState.Pressed)
            _scaleDragging = false;

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
        if (!IsLoaded || _loadingStage || _updatingBgPanSliders)
            return;

        SyncBackgroundPanSliderRanges();
        if (_bgTransformDragging)
            return;

        await ApplyBackgroundTransformChangeAsync();
    }

    private async void BgPanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _loadingStage || _updatingBgPanSliders)
            return;

        UpdateBackgroundTransformLabels();
        if (_bgTransformDragging)
            return;

        await ApplyBackgroundTransformChangeAsync();
    }

    /// <summary>
    /// Auto-match Cover background to subject only when both come from card art
    /// (live-art rembg/SAM subject + card-art background). Re-applies on subject drag/scale
    /// while that pairing holds; skipped for custom BG / Pick manually… subject.
    /// </summary>
    private bool ShouldAutoMatchBackgroundToSubject() =>
        _backgroundIsCardArt
        && _subjectIsFromCardArt
        && _backgroundSource is not null
        && _subjectSource is not null;

    /// <summary>
    /// Copies subject Cover scale/offset into background sliders and fields via Core
    /// <see cref="OverFrameAutoArtComposer.MatchBackgroundToSubject"/> (Pendulum Y bias).
    /// Writes BG controls under <see cref="_updatingBgPanSliders"/> so pan/scale handlers
    /// do not re-enter. Does not recompose; caller must refresh preview afterward.
    /// </summary>
    private bool TryApplyAutoMatchBackgroundTransforms()
    {
        if (!ShouldAutoMatchBackgroundToSubject() || _backgroundSource is null)
            return false;

        // Core Match accounts for PendulumVerticalOffset: subject Cover is nudged +200 on
        // Pendulum while hole-only Cover background is not — copying offset 1:1 misaligns.
        var (matchedScale, matchedPanX, matchedPanY) =
            OverFrameAutoArtComposer.MatchBackgroundToSubject(
                GetSubjectScale(),
                _offsetX,
                _offsetY,
                GetSelectedFrameStyle(),
                _backgroundSource.Width,
                _backgroundSource.Height);

        _updatingBgPanSliders = true;
        try
        {
            BgScaleSlider.Value = matchedScale;
        }
        finally
        {
            _updatingBgPanSliders = false;
        }

        // Pan limits depend on the new shared Cover scale — sync ranges first, then apply
        // matched pan (already clamped in Core; re-clamp to live slider max for safety).
        SyncBackgroundPanSliderRanges();
        int panX;
        int panY;
        _updatingBgPanSliders = true;
        try
        {
            panX = OverFrameAutoArtComposer.ClampBackgroundPan(
                matchedPanX, (int)Math.Round(BgPanHSlider.Maximum));
            panY = OverFrameAutoArtComposer.ClampBackgroundPan(
                matchedPanY, (int)Math.Round(BgPanVSlider.Maximum));
            BgPanHSlider.Value = panX;
            BgPanVSlider.Value = panY;
        }
        finally
        {
            _updatingBgPanSliders = false;
        }

        _backgroundScale = matchedScale;
        _backgroundOffsetX = panX;
        _backgroundOffsetY = panY;
        UpdateBackgroundTransformLabels();
        return true;
    }

    private async Task ApplyArtScaleChangeAsync()
    {
        if (_loadingStage)
            return;

        var scale = GetSubjectScale();
        if (Math.Abs(scale - _subjectScale) < 0.0001f
            && _subjectSource is not null
            && _composedTempPath is not null
            && !_pendingArtScaleApply)
        {
            return;
        }

        // Always keep the field in sync with the slider, even when compose is deferred.
        _subjectScale = scale;
        if (_subjectSource is null || _subjectMask is null)
            return;

        if (_busy)
        {
            _pendingArtScaleApply = true;
            return;
        }

        _pendingArtScaleApply = false;
        SetBusy(true);
        StatusText.Text = $"Recomposing at art scale ×{_subjectScale:0.00}…";
        try
        {
            var matched = TryApplyAutoMatchBackgroundTransforms();
            await RecomposePreviewAsync();
            StatusText.Text = matched
                ? $"Preview at scale ×{_subjectScale:0.00}, offset {_offsetX}, {_offsetY}; background matched. Drag or Apply."
                : $"Preview at scale ×{_subjectScale:0.00}, offset {_offsetX}, {_offsetY}. Drag or Apply.";
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
            _backgroundIsCardArt = false;
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
            var outputPath = liveTemp;
            await ExportIllustrationForEditingAsync(outputPath);

            DisposeBackground();
            _backgroundIsCardArt = true;
            _backgroundSource = await Task.Run(() => ImageSharpImage.Load<Rgba32>(liveTemp));
            ResetBackgroundPlacement();

            var matched = TryApplyAutoMatchBackgroundTransforms();
            var subjectStatus = readyStatus
                ?? (matched
                    ? $"Background: current card art (Cover), matched to card-art subject ×{_backgroundScale:0.00}. Drag Card Art or Apply."
                    : "Background: current card art (Cover). Preview updated — drag Card Art or Apply.");
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

    private async void SubjectAutoRadio_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _loadingStage)
            return;

        await PrepareSubjectFromLiveArtRembgAsync();
    }

    private async void SubjectManualRadio_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _loadingStage)
            return;

        await PickSubjectImageAsync();
    }

    private async void AddMoreSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !Sam2PointCutoutService.IsFeatureEnabled)
            return;

        await RunSam2PaintSelectionAsync();
    }

    /// <summary>
    /// Opens the SAM editor. When a subject is already loaded (including a restored
    /// Custom OF stage), seeds green/cyan selection from that mask (or subject alpha).
    /// Prefer the in-memory subject canvas so the mask aligns 1:1 with the SAM art.
    /// </summary>
    private async Task RunSam2PaintSelectionAsync()
    {
        SetBusy(true);
        string? liveTemp = null;
        Image<L8>? paintMask = null;
        Image<L8>? clickMask = null;
        Image<Rgba32>? clickSource = null;
        Image<L8>? derivedSelectionMask = null;
        try
        {
            liveTemp = Path.Combine(
                Path.GetTempPath(),
                $"floowan-custom-of-sam2-{Guid.NewGuid():N}.png");

            var selectionMask = ResolveSamExistingSelectionMask(out derivedSelectionMask);
            var usedSubjectCanvas = await PrepareSamArtCanvasAsync(liveTemp, selectionMask);
            StatusText.Text = usedSubjectCanvas
                ? "Opening SAM 2 with current subject selection…"
                : "Extracting card art for SAM 2…";

            var progress = new Progress<string>(msg => StatusText.Text = msg);
            // Download / SHA-256 / extract / ONNX session load — all off UI (Core).
            await _sam2Cutout.EnsureModelsAsync(progress);

            SetBusy(false);
            StatusText.Text = selectionMask is not null
                ? "SAM 2 editor: current subject loaded. Paint or Click to edit, then Apply."
                : "SAM 2 editor: Paint or Click object, then Apply selection.";
            var paintWindow = Sam2MaskPaintWindow.FromImagePath(
                liveTemp,
                _sam2Cutout,
                selectionMask);
            paintWindow.Owner = this;
            var accepted = paintWindow.ShowDialog() == true;
            if (!accepted)
            {
                StatusText.Text = "SAM 2 selection cancelled.";
                return;
            }

            Image<Rgba32> preparedSource;
            Image<L8> preparedMask;
            string modeNote;
            if (paintWindow.ResultKind == Sam2EditorResultKind.WorkingSamMask)
            {
                clickMask = paintWindow.ResultSamMask;
                clickSource = paintWindow.ResultSamSource;
                if (clickMask is null || clickSource is null)
                {
                    StatusText.Text = "SAM 2 click selection cancelled.";
                    return;
                }

                preparedSource = clickSource;
                preparedMask = clickMask;
                clickSource = null;
                clickMask = null;
                modeNote = "click";
            }
            else
            {
                paintMask = paintWindow.ResultPaintMask;
                if (paintMask is null)
                {
                    StatusText.Text = "SAM 2 paint selection cancelled.";
                    return;
                }

                SetBusy(true);
                StatusText.Text = "Running SAM 2 on painted region…";
                var prepared = await _sam2Cutout.PrepareSubjectWithPaintedRegionAsync(
                    liveTemp,
                    paintMask,
                    progress);
                preparedSource = prepared.Source;
                preparedMask = prepared.Mask;
                modeNote = "paint";
            }

            SetBusy(true);
            if (paintWindow.ResultKind == Sam2EditorResultKind.WorkingSamMask)
            {
                // Working selection already includes prior subject ± add/remove — replace mask.
                if (_subjectSource is not null
                    && _subjectMask is not null
                    && _subjectSource.Width == preparedSource.Width
                    && _subjectSource.Height == preparedSource.Height
                    && _subjectMask.Width == preparedMask.Width
                    && _subjectMask.Height == preparedMask.Height)
                {
                    _subjectMask.Dispose();
                    _subjectMask = preparedMask;
                    preparedSource.Dispose();
                }
                else
                {
                    if (_subjectSource is not null || _subjectMask is not null)
                        ResetSubjectPlacement();

                    _subjectSource = preparedSource;
                    _subjectMask = preparedMask;
                    _subjectIsFromCardArt = true;
                }

                // Empty working selection after removals → clear subject.
                if (!MaskHasOpaquePixels(_subjectMask))
                {
                    ResetSubjectPlacement();
                    if (_backgroundSource is not null)
                        await ShowBackgroundOnlyPreviewAsync();
                    StatusText.Text = "SAM 2 click selection cleared the subject.";
                    ApplyButton.IsEnabled = false;
                    return;
                }

                var matchedClick = TryApplyAutoMatchBackgroundTransforms();
                await RecomposePreviewAsync();
                StatusText.Text = matchedClick
                    ? $"Subject from SAM 2 click selection; background matched ×{_backgroundScale:0.00}. Drag or Apply."
                    : "Subject from SAM 2 click selection. Drag or scale the art, then Apply.";
                PreviewHintText.Visibility = Visibility.Collapsed;
                ApplyButton.IsEnabled = true;
            }
            else
            {
                var unioned = false;
                if (_subjectSource is not null
                    && _subjectMask is not null
                    && _subjectSource.Width == preparedSource.Width
                    && _subjectSource.Height == preparedSource.Height
                    && _subjectMask.Width == preparedMask.Width
                    && _subjectMask.Height == preparedMask.Height)
                {
                    var merged = Sam2PointCutoutService.UnionMasks(_subjectMask, preparedMask);
                    preparedMask.Dispose();
                    preparedSource.Dispose();
                    _subjectMask.Dispose();
                    _subjectMask = merged;
                    unioned = true;
                }
                else
                {
                    if (_subjectSource is not null || _subjectMask is not null)
                        ResetSubjectPlacement();

                    _subjectSource = preparedSource;
                    _subjectMask = preparedMask;
                    _subjectIsFromCardArt = true;
                }

                var matched = TryApplyAutoMatchBackgroundTransforms();
                await RecomposePreviewAsync();
                StatusText.Text = unioned
                    ? (matched
                        ? $"Added SAM 2 {modeNote} selection; background matched ×{_backgroundScale:0.00}. Drag or Apply."
                        : $"Added SAM 2 {modeNote} selection to existing subject. Drag or scale the art, then Apply.")
                    : (matched
                        ? $"Subject from SAM 2 {modeNote}; background matched ×{_backgroundScale:0.00}. Drag or Apply."
                        : $"Subject from SAM 2 {modeNote} selection. Drag or scale the art, then Apply.");
                PreviewHintText.Visibility = Visibility.Collapsed;
                ApplyButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            // Keep an existing subject when "Add more selection" fails mid-flight.
            if (_subjectSource is not null && _subjectMask is not null)
            {
                StatusText.Text = "SAM 2 add failed (existing subject kept): " + ex.Message;
                MessageBox.Show(
                    this,
                    "Could not add SAM 2 selection (existing subject was kept):\n\n" + ex.Message,
                    "Custom overframe art",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            else
            {
                await FailSubjectPrepareAsync(ex);
            }
        }
        finally
        {
            derivedSelectionMask?.Dispose();
            paintMask?.Dispose();
            clickMask?.Dispose();
            clickSource?.Dispose();
            if (liveTemp is not null && File.Exists(liveTemp))
            {
                try { File.Delete(liveTemp); } catch { /* ignore */ }
            }

            SetBusy(false);
        }
    }

    /// <summary>
    /// Prefers the in-memory subject canvas (restored stage / rembg / prior SAM) so the
    /// existing selection mask aligns 1:1 with the SAM art. Falls back to exporting
    /// illustration from game/backup.
    /// </summary>
    private async Task<bool> PrepareSamArtCanvasAsync(string outputPngPath, Image<L8>? selectionMask)
    {
        if (_subjectSource is not null
            && selectionMask is not null
            && _subjectSource.Width == selectionMask.Width
            && _subjectSource.Height == selectionMask.Height)
        {
            var source = _subjectSource;
            await Task.Run(() =>
                source.Save(outputPngPath, new SixLabors.ImageSharp.Formats.Png.PngEncoder()));
            return true;
        }

        await ExportIllustrationForEditingAsync(outputPngPath);
        return false;
    }

    /// <summary>
    /// Prefer the persisted/in-session L8 mask; else derive from subject RGBA alpha
    /// (Pick manually / stages that only carried subject alpha).
    /// </summary>
    private Image<L8>? ResolveSamExistingSelectionMask(out Image<L8>? ownedDerivedMask)
    {
        ownedDerivedMask = null;
        if (_subjectMask is not null && MaskHasOpaquePixels(_subjectMask))
            return _subjectMask;

        if (_subjectSource is null)
            return null;

        var derived = DeriveMaskFromSubjectAlpha(_subjectSource);
        if (derived is null)
            return null;

        ownedDerivedMask = derived;
        return derived;
    }

    private static Image<L8>? DeriveMaskFromSubjectAlpha(Image<Rgba32> subject)
    {
        var mask = new Image<L8>(subject.Width, subject.Height);
        var keep = 0;
        for (var y = 0; y < subject.Height; y++)
        {
            var pixels = subject.DangerousGetPixelRowMemory(y).Span;
            var row = mask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < pixels.Length; x++)
            {
                var alpha = pixels[x].A;
                row[x] = new L8(alpha);
                if (alpha >= OverFrameAutoArtComposer.MaskKeepThreshold)
                    keep++;
            }
        }

        if (keep == 0)
        {
            mask.Dispose();
            return null;
        }

        return mask;
    }

    private static bool MaskHasOpaquePixels(Image<L8> mask)
    {
        var threshold = OverFrameAutoArtComposer.MaskKeepThreshold;
        for (var y = 0; y < mask.Height; y++)
        {
            for (var x = 0; x < mask.Width; x++)
            {
                if (mask[x, y].PackedValue >= threshold)
                    return true;
            }
        }

        return false;
    }

    private async Task PickSubjectImageAsync()
    {
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
            _subjectIsFromCardArt = false;
            SubjectManualRadio.IsChecked = true;

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
    /// subject layer (same preview path as Pick manually…).
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
            StatusText.Text = "Extracting card art…";
            var outputPath = liveTemp;
            await ExportIllustrationForEditingAsync(outputPath);

            var progress = new Progress<string>(msg => StatusText.Text = msg);
            var prepared = await _autoArt.PrepareSubjectWithRembgAsync(liveTemp, progress);
            _subjectSource = prepared.Source;
            _subjectMask = prepared.Mask;
            _subjectIsFromCardArt = true;
            SubjectAutoRadio.IsChecked = true;

            var matched = TryApplyAutoMatchBackgroundTransforms();
            await RecomposePreviewAsync();
            StatusText.Text = matched
                ? $"Subject from live art (rembg); background matched ×{_backgroundScale:0.00}. Drag or Apply."
                : "Subject from live art (rembg). Drag or scale the art, then Apply.";
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
        _subjectIsFromCardArt = false;
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
        if (!IsLoaded || _busy || _loadingStage)
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
                    $"Background preview ({CardTypeLabels.ToLabel(CardFrameTemplates.GetSolidBaseStyle(GetSelectedFrameStyle()))}). Pick a subject…";
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
                $"Preview updated ({CardTypeLabels.ToLabel(CardFrameTemplates.GetSolidBaseStyle(GetSelectedFrameStyle()))}). Drag or scale the art, then Apply.";
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

        // Encode on a worker; build BitmapImages on the UI thread (WPF STA).
        byte[] basePng;
        byte[] subjectPng;
        try
        {
            (basePng, subjectPng) = await Task.Run(() =>
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
                return (EncodePreviewPng(baseLayer), EncodePreviewPng(subjectLayer));
            });
        }
        catch
        {
            // Keep the full composed preview; FinishDrag still recomposes on release.
            return;
        }

        if (generation != _dragVisualGeneration || !_dragging)
            return;

        PreviewImage.Source = BitmapImageFromPngBytes(basePng);
        SubjectOverlay.Source = BitmapImageFromPngBytes(subjectPng);
        ResetSubjectDragTransform();
        SubjectOverlay.Visibility = Visibility.Visible;
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_subjectSource is null || _subjectMask is null || _busy || PreviewImage.Source is null)
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
        if (PreviewHost.IsMouseCaptured)
            PreviewHost.ReleaseMouseCapture();

        var scale = GetPreviewCanvasScale();
        if (scale > 0)
        {
            var dx = (pos.X - _dragStart.X) / scale;
            var dy = (pos.Y - _dragStart.Y) / scale;
            _offsetX = _dragStartOffsetX + (int)Math.Round(dx);
            _offsetY = _dragStartOffsetY + (int)Math.Round(dy);
        }

        if (_subjectSource is null || _subjectMask is null)
            return;

        if (_busy)
        {
            // Offsets are already applied — recompose when the current busy op finishes.
            _pendingSubjectRecompose = true;
            return;
        }

        await RecomposeAfterSubjectTransformAsync(
            $"Recomposing at offset {_offsetX}, {_offsetY}…");
    }

    private async Task RecomposeAfterSubjectTransformAsync(string busyStatus)
    {
        _pendingSubjectRecompose = false;
        SetBusy(true);
        StatusText.Text = busyStatus;
        try
        {
            var matched = TryApplyAutoMatchBackgroundTransforms();
            await RecomposePreviewAsync();
            StatusText.Text = matched
                ? $"Preview at scale ×{_subjectScale:0.00}, offset {_offsetX}, {_offsetY}; background matched. Drag or Apply."
                : $"Preview at scale ×{_subjectScale:0.00}, offset {_offsetX}, {_offsetY}. Drag or Apply.";
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
        if (bmp is null || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0)
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

            try
            {
                SaveEditableStage();
            }
            catch
            {
                /* best-effort stage snapshot for reopen-edit */
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

    private void SaveEditableStage()
    {
        var frameStyle = GetSelectedFrameStyle();
        var state = new CustomOverframeStageState
        {
            FrameStyle = frameStyle.ToString(),
            SubjectScale = _subjectScale,
            SubjectOffsetX = _offsetX,
            SubjectOffsetY = _offsetY,
            BackgroundScale = _backgroundScale,
            BackgroundOffsetX = _backgroundOffsetX,
            BackgroundOffsetY = _backgroundOffsetY,
            BackgroundIsCardArt = _backgroundIsCardArt,
            SubjectIsFromCardArt = _subjectIsFromCardArt
        };

        _overFrameService.Backups.SaveCustomOverframeStage(
            _card.Name,
            state,
            _subjectSource,
            _subjectMask,
            _backgroundSource);
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
        SubjectAutoRadio.IsEnabled = !busy;
        SubjectManualRadio.IsEnabled = !busy;
        AddMoreSelectionButton.IsEnabled = !busy && Sam2PointCutoutService.IsFeatureEnabled;
        FrameStyleBox.IsEnabled = !busy;
        ArtScaleSlider.IsEnabled = !busy;
        BgScaleSlider.IsEnabled = !busy;
        if (!busy)
        {
            // Disabling the Art scale thumb mid-drag can skip DragCompleted; clear so
            // ValueChanged can Apply again once idle.
            _scaleDragging = false;
            _bgTransformDragging = false;
            SyncBackgroundPanSliderRanges();
            // Defer flush so we don't re-enter SetBusy(true) mid SetBusy(false).
            Dispatcher.BeginInvoke(
                FlushPendingTransformApplies,
                System.Windows.Threading.DispatcherPriority.Background);
        }
        else
        {
            BgPanHSlider.IsEnabled = false;
            BgPanVSlider.IsEnabled = false;
        }
        ApplyButton.IsEnabled = !busy && _subjectSource is not null && _composedTempPath is not null;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    /// <summary>
    /// Applies transform changes that were deferred while <see cref="_busy"/> was true.
    /// </summary>
    private void FlushPendingTransformApplies()
    {
        if (_loadingStage || _busy)
            return;

        if (_pendingArtScaleApply)
        {
            _ = ApplyArtScaleChangeAsync();
            return;
        }

        if (_pendingSubjectRecompose
            && _subjectSource is not null
            && _subjectMask is not null)
        {
            _ = RecomposeAfterSubjectTransformAsync(
                $"Recomposing at offset {_offsetX}, {_offsetY}…");
        }
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
        _subjectIsFromCardArt = false;
    }

    private void DisposeBackground()
    {
        _backgroundSource?.Dispose();
        _backgroundSource = null;
        _backgroundIsCardArt = false;
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

    private static byte[] EncodePreviewPng(Image<Rgba32> image)
    {
        using var flat = OverFrameAutoArtComposer.FlattenFoilMaskForPreview(image);
        using var ms = new MemoryStream();
        flat.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        return ms.ToArray();
    }

    private static BitmapImage BitmapImageFromPngBytes(byte[] png)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static BitmapImage ToPreviewBitmap(Image<Rgba32> image) =>
        BitmapImageFromPngBytes(EncodePreviewPng(image));
}
