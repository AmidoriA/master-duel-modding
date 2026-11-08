using System.Windows.Controls;
using Floowan.Core.Imaging;
using Floowan.Desktop.Localization;

namespace Floowan.Desktop;

/// <summary>
/// Cutout model picker under Custom OF "Auto detect": isnet-anime or SAM 3 + ViTMatte. It edits
/// the same setting as Over-frame → Subject cutout (the shared
/// <see cref="AutoOverFrameArtService"/> and user.db), so both places stay in step.
/// </summary>
public partial class CustomOverframeWindow
{
    private bool _cutoutPickerLoading;

    /// <summary>True when the cutout model changed in this window.</summary>
    public bool CutoutSettingsChanged { get; private set; }

    private void InitCutoutPicker()
    {
        _cutoutPickerLoading = true;
        try
        {
            CutoutModelLabel.Text = Loc.T("custom_of.cutout_model");
            CutoutModelBox.ToolTip = Loc.T("overframe.cutout_tooltip");
            CutoutIsnetItem.Content = Loc.T("overframe.cutout_isnet");
            // Short label: the panel is narrow; the download size shows on first use.
            CutoutSam3Item.Content = Loc.T("custom_of.cutout_sam3");
            CutoutModelBox.SelectedItem = _autoArt.CutoutModel == SubjectCutoutModel.Sam3
                ? CutoutSam3Item
                : CutoutIsnetItem;
        }
        finally
        {
            _cutoutPickerLoading = false;
        }
    }

    private SubjectCutoutModel SelectedCutoutModel =>
        CutoutModelBox.SelectedItem is ComboBoxItem { Tag: string id }
            ? SubjectCutoutModels.FromId(id)
            : SubjectCutoutModel.IsnetAnime;

    private void UpdateCutoutPickerEnabled() => CutoutModelBox.IsEnabled = !_busy;

    private async void CutoutModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_cutoutPickerLoading)
            return;

        var model = SelectedCutoutModel;
        if (model == _autoArt.CutoutModel)
            return;

        _autoArt.CutoutModel = model;
        _database?.SetStoredCutoutModel(SubjectCutoutModels.ToId(model));
        CutoutSettingsChanged = true;
        await RerunAutoDetectAfterCutoutChangeAsync();
    }

    /// <summary>
    /// When the current subject is an Auto detect cutout, cut it out again with the new model
    /// (like clicking Auto detect). Otherwise only say which model the next Auto detect will
    /// use, so a picked PNG is never replaced.
    /// </summary>
    private async Task RerunAutoDetectAfterCutoutChangeAsync()
    {
        if (_busy || _loadingStage)
            return;

        if (SubjectAutoRadio.IsChecked == true && _subjectIsFromCardArt)
        {
            await PrepareSubjectFromLiveArtRembgAsync();
            return;
        }

        StatusText.Text = $"Cutout model: {_autoArt.CutoutModelLabel}. Click Auto detect to use it.";
    }
}
