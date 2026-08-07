using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Floowan.Desktop.Localization;

namespace Floowan.Desktop;

public partial class MainWindow
{
    private bool _optionsLanguageUpdating;

    private void ApplyLocalizedUi()
    {
        Title = Loc.T("app.title");

        HomeTitleText.Text = Loc.T("app.title");
        HomeSubtitleText.Text = Loc.T("app.subtitle");
        HomeLocalDataLabel.Text = Loc.T("home.localdata_path");
        DiscoverSteamButton.Content = Loc.T("home.discover_steam");
        BrowseLocalDataButton.Content = Loc.T("home.browse_localdata");

        if (StatusText.Text == "Ready" || StatusText.Text == Loc.T("status.ready") ||
            string.IsNullOrWhiteSpace(StatusText.Text))
            StatusText.Text = Loc.T("status.ready");

        CardArtTab.Header = Loc.T("tabs.card_art");
        OverFrameTab.Header = Loc.T("tabs.overframe");
        DatabaseTab.Header = Loc.T("tabs.database");
        ToolsTab.Header = Loc.T("tabs.tools");
        ImportExportTab.Header = Loc.T("tabs.import_export");
        OptionsTab.Header = Loc.T("tabs.options");
        AboutTab.Header = Loc.T("tabs.about");

        CardArtCardsHeader.Text = Loc.T("card_art.cards");
        CardArtSearchButton.Content = Loc.T("common.search");
        FavoritesOnlyBox.Content = Loc.T("common.favorites");
        SearchDescBox.Content = Loc.T("common.include_description");
        CardArtViewLabel.Text = Loc.T("common.view");
        CardArtViewListRadio.Content = Loc.T("common.list");
        CardArtViewThumbRadio.Content = Loc.T("common.thumbnails");
        CardArtSelectedHeader.Text = Loc.T("card_art.selected_card");
        SelectImageButton.Content = Loc.T("card_art.select_image");
        ReplaceArtButton.Content = Loc.T("card_art.replace_art");
        ReplaceArtButton.ToolTip = Loc.T("card_art.replace_art_tooltip");
        ExtractPngButton.Content = Loc.T("card_art.extract_png");
        RestoreBackupButton.Content = Loc.T("card_art.restore_backup");
        CardArtOpenOfButton.Content = Loc.T("common.open_in_overframe");
        CardArtOpenOfButton.ToolTip = Loc.T("card_art.open_in_overframe_tooltip");
        CardArtOpenDbButton.Content = Loc.T("common.open_in_database");
        CardArtOpenDbButton.ToolTip = Loc.T("card_art.open_in_database_tooltip");
        CurrentArtImage.ToolTip = Loc.T("app.click_to_enlarge");
        ReplacementImage.ToolTip = Loc.T("app.click_to_enlarge");
        if (_selected is null)
            CardTitleText.Text = Loc.T("app.none_selected");

        OfCardsHeader.Text = Loc.T("overframe.cards");
        if (!_ofGateReady && (OfGateStatusText.Text.Contains("not scanned", StringComparison.OrdinalIgnoreCase) ||
                              OfGateStatusText.Text == Loc.T("overframe.gate_not_scanned")))
            OfGateStatusText.Text = Loc.T("overframe.gate_not_scanned");
        OfSearchButton.Content = Loc.T("common.search");
        OfOverframeOnlyBox.Content = Loc.T("overframe.overframe_only");
        OfFavoritesOnlyBox.Content = Loc.T("common.favorites");
        OfSearchDescBox.Content = Loc.T("common.include_description");
        OfViewLabel.Text = Loc.T("common.view");
        OfViewListRadio.Content = Loc.T("common.list");
        OfViewThumbRadio.Content = Loc.T("common.thumbnails");
        OfEditorHeader.Text = Loc.T("overframe.editor");
        OfAutoCreateButton.Content = Loc.T("overframe.auto_create_apply");
        OfPreviewButton.Content = Loc.T("overframe.preview");
        OfRestoreButton.Content = Loc.T("overframe.restore_backups");
        OfOpenCardArtButton.Content = Loc.T("common.open_in_card_art");
        OfOpenCardArtButton.ToolTip = Loc.T("overframe.open_in_card_art_tooltip");
        OfOpenDbButton.Content = Loc.T("common.open_in_database");
        OfOpenDbButton.ToolTip = Loc.T("overframe.open_in_database_tooltip");
        OfAdvancedExpander.Header = Loc.T("overframe.advanced_header");
        OfAdvancedExpander.ToolTip = Loc.T("overframe.advanced_tooltip");
        OfEnableGateButton.Content = Loc.T("overframe.enable_gate");
        OfForceDisableGateButton.Content = Loc.T("overframe.force_disable_gate");
        OfForceDisableGateButton.ToolTip = Loc.T("overframe.force_disable_tooltip");
        OfFrameLabel.Text = Loc.T("overframe.frame");
        LocalizeFrameCombo(OfFrameStyleBox);
        OfCurrentArtImage.ToolTip = Loc.T("app.click_to_enlarge");
        OfReplacementImage.ToolTip = Loc.T("app.click_to_enlarge");
        if (_ofSelected is null)
            OfCardTitleText.Text = Loc.T("app.none_selected");
        UpdateOfCustomOverframeActionUi();

        DbFiltersHeader.Text = Loc.T("database.filters");
        DbFilterIdLabel.Text = Loc.T("database.card_id");
        DbFilterNameLabel.Text = Loc.T("database.name");
        DbFilterDescLabel.Text = Loc.T("database.description");
        DbFilterFavoriteLabel.Text = Loc.T("database.favorite");
        DbFilterBackupLabel.Text = Loc.T("database.backup");
        DbFilterModdedNameLabel.Text = Loc.T("database.modded_name");
        DbFilterModdedDescLabel.Text = Loc.T("database.modded_desc");
        LocalizeAnyYesNo(DbFilterFavoriteBox);
        LocalizeBackupFilter(DbFilterBackupBox);
        LocalizePopulatedEmpty(DbFilterModdedNameBox);
        LocalizePopulatedEmpty(DbFilterModdedDescBox);
        DbApplyFiltersButton.Content = Loc.T("database.apply_filters");
        DbClearFiltersButton.Content = Loc.T("database.clear_filters");
        DbPrevPageButton.Content = Loc.T("database.prev_page");
        DbNextPageButton.Content = Loc.T("database.next_page");
        DbCardsHeader.Text = Loc.T("database.cards");
        DbRecordHeader.Text = Loc.T("database.record_details");
        DbOpenCardArtButton.Content = Loc.T("common.open_in_card_art");
        DbOpenOfButton.Content = Loc.T("common.open_in_overframe");
        DbEditFavoriteBox.Content = Loc.T("database.favorite_checkbox");
        DbArtHeader.Text = Loc.T("database.card_art");
        if (_dbSelected is null)
            DbArtMetaText.Text = Loc.T("database.select_to_preview");

        ToolsDbHeader.Text = Loc.T("tools.card_database");
        ToolsDbHelpText.Text = Loc.T("tools.card_database_help");
        ToolsUpdateEntireDbButton.Content = Loc.T("tools.update_entire_db");
        ToolsUpdateNewFilesDbButton.Content = Loc.T("tools.update_new_files");
        ToolsUpdateNewFilesDbButton.ToolTip = Loc.T("tools.update_new_files_tooltip");
        ToolsOfGateHeader.Text = Loc.T("tools.of_gate");
        ToolsOfGateHelpText.Text = Loc.T("tools.of_gate_help");
        ToolsScanOfCardAssetButton.Content = Loc.T("tools.scan_of_card");
        ToolsScanOfCardAssetButton.ToolTip = Loc.T("tools.scan_of_card_tooltip");
        ToolsOfAfterPatchHeader.Text = Loc.T("tools.of_after_patch");
        ToolsOfAfterPatchHelpText.Text = Loc.T("tools.of_after_patch_help");
        ToolsRestoreOverframesButton.Content = Loc.T("tools.restore_overframes");
        ToolsRestoreOverframesButton.ToolTip = Loc.T("tools.restore_overframes_tooltip");
        ToolsOrphanOverframesButton.Content = Loc.T("tools.orphan_fix");
        ToolsOrphanOverframesButton.ToolTip = Loc.T("tools.orphan_fix_tooltip");
        ToolsOnnxHeader.Text = Loc.T("tools.onnx_models");
        ToolsOnnxHelpText.Text = Loc.T("tools.onnx_help");
        ToolsOpenModelsDirButton.Content = Loc.T("tools.open_models");
        ToolsOpenModelsDirButton.ToolTip = Loc.T("tools.open_models_tooltip");
        ToolsBackupsHeader.Text = Loc.T("tools.backups");
        ToolsBackupsHelpText.Text = Loc.T("tools.backups_help");
        ToolsBackupFolderLabel.Text = Loc.T("tools.backup_folder");
        ToolsOpenBackupRootButton.Content = Loc.T("tools.open_explorer");
        ToolsRefreshBackupsButton.Content = Loc.T("common.refresh");
        ToolsOpenSelectedButton.Content = Loc.T("tools.open_selected");

        ImportExportHeader.Text = Loc.T("import_export.header");
        ImportExportHelpText.Text = Loc.T("import_export.help");
        ImportExportExportButton.Content = Loc.T("import_export.export");
        ImportExportExportButton.ToolTip = Loc.T("import_export.export_tooltip");
        ImportExportImportButton.Content = Loc.T("import_export.import");
        ImportExportImportButton.ToolTip = Loc.T("import_export.import_tooltip");

        OptionsHeader.Text = Loc.T("options.title");
        OptionsIntroText.Text = Loc.T("options.intro");
        OptionsLanguageLabel.Text = Loc.T("options.language");
        OptionsLanguageHelpText.Text = Loc.T("options.language_help");
        OptionsLanguageAutoNoteText.Text = Loc.T("options.language_auto_note");
        OptionsReloadLocalesButton.Content = Loc.T("options.reload_locales");
        OptionsReloadLocalesButton.ToolTip = Loc.T("options.reload_locales_tooltip");
        OptionsLocalesPathLabel.Text = Loc.T("options.locales_path");
        OptionsOpenLocalesButton.Content = Loc.T("options.open_locales");
        OptionsLocalesPathBox.Text = Loc.LocalesDirectory;
        PopulateLanguageCombo();

        AboutTitleText.Text = Loc.T("app.title");
        var version = typeof(App).Assembly.GetName().Version;
        AboutVersionText.Text = Loc.T("app.version", version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}");
        AboutTaglineText.Text = Loc.T("app.tagline");
        AboutOpenGitHubButton.Content = Loc.T("about.open_github");
        AboutCreditsTitleText.Text = Loc.T("about.credits_title");
        AboutCreditsBeforeRun.Text = Loc.T("about.credits_before");
        AboutCreditsNameRun.Text = Loc.T("about.credits_name");
        AboutCreditsAfterRun.Text = Loc.T("about.credits_after");
        AboutOpenFloowanButton.Content = Loc.T("about.open_floowan");
        AboutThirdPartyHeader.Text = Loc.T("about.third_party");
        AboutThirdPartyHelpText.Text = Loc.T("about.third_party_help");
        AboutOpenThirdPartyButton.Content = Loc.T("about.open_third_party");

        BusyWorkingText.Text = Loc.T("app.working");
        if (string.IsNullOrWhiteSpace(BusyStatusText.Text) ||
            BusyStatusText.Text is "Please wait…" or "กรุณารอสักครู่…")
            BusyStatusText.Text = Loc.T("app.please_wait");
    }

    private static void LocalizeFrameCombo(ComboBox box)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag)
                item.Content = Loc.T("frames." + tag);
        }
    }

    private static void LocalizeAnyYesNo(ComboBox box)
    {
        if (box.Items.Count < 3) return;
        SetComboContent(box, 0, Loc.T("common.any"));
        SetComboContent(box, 1, Loc.T("common.yes"));
        SetComboContent(box, 2, Loc.T("common.no"));
    }

    private static void LocalizeBackupFilter(ComboBox box)
    {
        if (box.Items.Count < 3) return;
        SetComboContent(box, 0, Loc.T("common.any"));
        SetComboContent(box, 1, Loc.T("database.has_backup"));
        SetComboContent(box, 2, Loc.T("database.no_backup"));
    }

    private static void LocalizePopulatedEmpty(ComboBox box)
    {
        if (box.Items.Count < 3) return;
        SetComboContent(box, 0, Loc.T("common.any"));
        SetComboContent(box, 1, Loc.T("database.populated"));
        SetComboContent(box, 2, Loc.T("database.empty"));
    }

    private static void SetComboContent(ComboBox box, int index, string text)
    {
        if (box.Items[index] is ComboBoxItem item)
            item.Content = text;
    }

    private void PopulateLanguageCombo()
    {
        _optionsLanguageUpdating = true;
        try
        {
            OptionsLanguageCombo.Items.Clear();
            foreach (var culture in Loc.AvailableCultures)
            {
                OptionsLanguageCombo.Items.Add(new ComboBoxItem
                {
                    Content = FormatCultureLabel(culture),
                    Tag = culture
                });
            }

            foreach (ComboBoxItem item in OptionsLanguageCombo.Items)
            {
                if (item.Tag is string tag &&
                    string.Equals(tag, Loc.Culture, StringComparison.OrdinalIgnoreCase))
                {
                    OptionsLanguageCombo.SelectedItem = item;
                    break;
                }
            }
        }
        finally
        {
            _optionsLanguageUpdating = false;
        }
    }

    private static string FormatCultureLabel(string culture) => culture switch
    {
        "en-US" => "English (en-US)",
        "th-TH" => "ไทย (th-TH)",
        _ => culture
    };

    private void OptionsLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_optionsLanguageUpdating) return;
        if (OptionsLanguageCombo.SelectedItem is not ComboBoxItem { Tag: string culture }) return;
        if (string.Equals(culture, Loc.Culture, StringComparison.OrdinalIgnoreCase)) return;

        Loc.SetCulture(culture);
        _database?.SetStoredLocale(culture);
        ApplyLocalizedUi();
        Status(Loc.T("status.language_changed", culture));
    }

    private void OptionsReloadLocales_Click(object sender, RoutedEventArgs e)
    {
        Loc.Service.Reload();
        // Keep current culture if still available; otherwise fall back.
        Loc.SetCulture(Loc.Culture);
        ApplyLocalizedUi();
        Status(Loc.T("options.reloaded", string.Join(", ", Loc.AvailableCultures)));
    }

    private void OptionsOpenLocales_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Loc.LocalesDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = Loc.LocalesDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Loc.T("app.caption"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string AppCaption => Loc.T("app.caption");
    private string OfRestartHint => Loc.T("app.restart_hint");
}
