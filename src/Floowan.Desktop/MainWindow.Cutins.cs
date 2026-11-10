using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Floowan.Core.Assets;
using Floowan.Core.Cutins;
using Floowan.Core.Game;
using Floowan.Core.Models;
using Floowan.Desktop.Localization;
using Microsoft.Win32;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Desktop;

/// <summary>Cut-ins tab: video Monster Cut-ins (summon animations), restore after patches, .cutins packs.</summary>
public partial class MainWindow
{
    public sealed class CutinListItem
    {
        public int CardId { get; init; }
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string StatusText { get; init; } = "";
    }

    private static readonly HttpClient CutinHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer _cutinTimer = new() { Interval = TimeSpan.FromSeconds(1 / 30.0) };
    private BitmapSource[] _cutinPreviewFrames = [];
    private int _cutinPreviewIndex;
    private string? _cutinPreviewDir;
    private CardRecord? _cutinPreviewCard;
    private HashSet<int>? _cutinOfficialIds;
    private bool _cutinTimerHooked;

    /// <summary>
    /// The Cut-ins tab is not production ready yet: hidden unless FLOOWAN_CUTINS=1 is set (for testing).
    /// Tools > After game patch still restores cut-ins that are already registered.
    /// </summary>
    private static bool CutinTabEnabled => Environment.GetEnvironmentVariable("FLOOWAN_CUTINS") == "1";

    private void ApplyCutinLocalizedUi()
    {
        CutinTab.Visibility = CutinTabEnabled ? Visibility.Visible : Visibility.Collapsed;
        CutinTab.Header = Loc.T("tabs.cutins");
        CutinInstalledHeader.Text = Loc.T("cutins.installed_header");
        CutinInstalledHelpText.Text = Loc.T("cutins.installed_help");
        CutinRefreshButton.Content = Loc.T("cutins.refresh");
        CutinRestoreAllButton.Content = Loc.T("cutins.restore_all");
        CutinRestoreAllButton.ToolTip = Loc.T("cutins.restore_all_tooltip");
        CutinRemoveButton.Content = Loc.T("cutins.remove");
        CutinRemoveButton.ToolTip = Loc.T("cutins.remove_tooltip");
        CutinExportButton.Content = Loc.T("cutins.export");
        CutinExportButton.ToolTip = Loc.T("cutins.export_tooltip");
        CutinImportButton.Content = Loc.T("cutins.import");
        CutinImportButton.ToolTip = Loc.T("cutins.import_tooltip");
        CutinColCard.Header = Loc.T("cutins.col_card");
        CutinColKind.Header = Loc.T("cutins.col_kind");
        CutinColStatus.Header = Loc.T("cutins.col_status");
        CutinNewHeader.Text = Loc.T("cutins.new_header");
        CutinNewHelpText.Text = Loc.T("cutins.new_help");
        CutinCardLabel.Text = Loc.T("cutins.card");
        CutinSearchButton.Content = Loc.T("common.search");
        CutinVideoLabel.Text = Loc.T("cutins.video");
        CutinBrowseVideoButton.Content = Loc.T("cutins.browse");
        CutinTrimLabel.Text = Loc.T("cutins.trim_from");
        CutinTrimToLabel.Text = Loc.T("cutins.trim_to");
        CutinTrimEndLabel.Text = Loc.T("cutins.trim_end_hint");
        CutinEndBox.ToolTip = Loc.T("cutins.trim_end_tooltip");
        CutinReplaceOfficialBox.Content = Loc.T("cutins.replace_official");
        CutinBuildPreviewButton.Content = Loc.T("cutins.build_preview");
        CutinInstallButton.Content = Loc.T("cutins.install");
        CutinInstallButton.ToolTip = Loc.T("cutins.install_tooltip");
        CutinNameBarText.Text = Loc.T("cutins.name_bar");
        CutinPreviewEmptyText.Text = Loc.T("cutins.preview_empty");
        CutinPlayButton.Content = _cutinTimer.IsEnabled ? Loc.T("cutins.pause") : Loc.T("cutins.play");
        CutinShowNameBarBox.Content = Loc.T("cutins.show_name_bar");
    }

    private void OnCutinTabSelected()
    {
        if (!_cutinTimerHooked)
        {
            _cutinTimer.Tick += CutinTimer_Tick;
            _cutinTimerHooked = true;
        }
        if (CutinList.Items.Count == 0 && !string.IsNullOrWhiteSpace(GamePathBox.Text))
            _ = RefreshCutinListAsync(quiet: true);
    }

    private bool TryGetCutinGamePath(out string gamePath, bool quiet = false)
    {
        gamePath = GamePathBox.Text?.Trim() ?? "";
        string? error = null;
        if (!string.IsNullOrWhiteSpace(gamePath) && GamePathLocator.IsValidGamePath(gamePath, out error))
            return true;
        if (!quiet)
            MessageBox.Show(string.IsNullOrWhiteSpace(gamePath) ? Loc.T("common.set_localdata_home") : error ?? Loc.T("common.set_localdata_home"), AppCaption);
        return false;
    }

    private static CutinInstaller CreateCutinInstaller(string gamePath, ICutinTextureEncoder? encoder = null) =>
        new(CutinRegistry.Load(), gamePath, ClassDataLocator.FindClassDataPath(), encoder);

    private string CutinCardName(int id, string? stored)
    {
        var card = _database?.GetById(id);
        return card?.DisplayName ?? stored ?? id.ToString(CultureInfo.InvariantCulture);
    }

    private async Task RefreshCutinListAsync(bool quiet = false)
    {
        if (!TryGetCutinGamePath(out var gamePath, quiet))
            return;
        try
        {
            var (statuses, official, kinds) = await Task.Run(() =>
            {
                var inst = CreateCutinInstaller(gamePath);
                var st = inst.Status();
                return (st, OfficialIds(inst), st.ToDictionary(s => s.Entry.CardId, s => CutinKind(inst.Registry, s.Entry)));
            });
            _cutinOfficialIds = official;
            var items = statuses.Select(s => new CutinListItem
            {
                CardId = s.Entry.CardId,
                Name = CutinCardName(s.Entry.CardId, s.Entry.Name),
                Kind = kinds[s.Entry.CardId],
                StatusText = s.State switch
                {
                    CutinState.Ok => Loc.T("cutins.state_ok"),
                    CutinState.NeedsRestore => Loc.T("cutins.state_restore"),
                    _ => Loc.T("cutins.state_official"),
                },
            }).ToList();
            CutinList.ItemsSource = items;
            var needs = statuses.Count(s => s.State == CutinState.NeedsRestore);
            CutinListStatusText.Text = needs > 0
                ? Loc.T("cutins.list_needs_restore", items.Count, needs)
                : Loc.T("cutins.list_count", items.Count);
            UpdateCutinCardInfo();
        }
        catch (Exception ex)
        {
            CutinListStatusText.Text = ex.Message;
            if (!quiet)
                MessageBox.Show(ex.Message, AppCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex OfficialRootPattern =
        new(@"^Duel/Timeline/Duel/MonsterCutIn/tcg/P(\d+)/SD/P\1$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Cards the unpatched game has a cut-in for (its own table and manifest, before our changes).</summary>
    private static HashSet<int> OfficialIds(CutinInstaller inst)
    {
        var tableLive = CutinNaming.BundleFile(inst.PlayerDir, CutinNaming.TablePath);
        var manLive = CutinNaming.BundleFile(inst.PlayerDir, CutinNaming.ManifestName);
        var table = inst.Registry.PristineOf(tableLive)?.File is { } t && File.Exists(t) ? t : tableLive;
        var man = inst.Registry.PristineOf(manLive)?.File is { } m && File.Exists(m) ? m : manLive;
        var ids = inst.ReadTableIds(table).ToHashSet();
        foreach (var e in CutinInstaller.ReadManifest(man)["informations"]!.AsArray())
        {
            var match = OfficialRootPattern.Match(e!["assetName"]!.GetValue<string>());
            if (match.Success && int.TryParse(match.Groups[1].Value, out var id))
                ids.Add(id);
        }
        return ids;
    }

    private static string CutinKind(CutinRegistry reg, CutinEntry e)
    {
        if (!e.IsCustom)
            return Loc.T("cutins.kind_borrow", e.Source ?? 0);
        return IsVideoEntry(reg, e) ? Loc.T("cutins.kind_video") : Loc.T("cutins.kind_custom");
    }

    /// <summary>A custom cut-in made from a video (by this app: video.json; by video_cutin.py: meta "source": "video").</summary>
    private static bool IsVideoEntry(CutinRegistry reg, CutinEntry e)
    {
        try
        {
            var dir = reg.ContentDir(e);
            if (File.Exists(Path.Combine(dir, "video.json")))
                return true;
            var meta = Path.Combine(dir, "meta.json");
            if (!File.Exists(meta))
                return false;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(meta));
            return doc.RootElement.TryGetProperty("source", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String && s.GetString() == "video";
        }
        catch
        {
            return false;
        }
    }

    private async void CutinRefresh_Click(object sender, RoutedEventArgs e) => await RefreshCutinListAsync();

    // ------------------------------------------------------------------ card + video

    private void CutinSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CutinSearch_Click(sender, e);
    }

    private void CutinSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null)
        {
            Status(Loc.T("common.open_database_first"));
            return;
        }
        CutinCardList.ItemsSource = _database.SearchCards(CutinSearchBox.Text, limit: 200);
    }

    private void CutinCardList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCutinCardInfo();

    private void UpdateCutinCardInfo()
    {
        if (CutinCardList.SelectedItem is not CardRecord card)
        {
            CutinCardInfoText.Text = "";
            CutinReplaceOfficialBox.IsEnabled = false;
            CutinReplaceOfficialBox.IsChecked = false;
            return;
        }
        var parts = new List<string> { Loc.T("cutins.card_id", card.Id) };
        var official = _cutinOfficialIds?.Contains(card.Id) == true;
        var registered = CutinRegistry.Load().Get(card.Id);
        if (official)
            parts.Add(Loc.T("cutins.card_has_official"));
        if (registered is not null)
            parts.Add(Loc.T("cutins.card_has_custom"));
        if (!official && registered is null)
            parts.Add(Loc.T("cutins.card_has_none"));
        CutinCardInfoText.Text = string.Join(" · ", parts);
        CutinReplaceOfficialBox.IsEnabled = official;
        if (!official)
            CutinReplaceOfficialBox.IsChecked = false;
        if (_cutinPreviewCard is not null && _cutinPreviewCard.Id != card.Id)
            CutinInstallButton.IsEnabled = false;
    }

    private void CutinBrowseVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("cutins.pick_video_title"),
            Filter = Loc.T("cutins.video_filter") + "|*.mp4;*.m4v;*.mov;*.webm;*.mkv;*.avi;*.wmv|" + Loc.T("cutins.all_files") + "|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            CutinVideoPathBox.Text = dlg.FileName;
            CutinInstallButton.IsEnabled = false;
        }
    }

    private void CutinVideoPath_Changed(object sender, TextChangedEventArgs e)
    {
        if (CutinInstallButton is not null)
            CutinInstallButton.IsEnabled = false;
    }

    private bool TryReadTrim(out double start, out double? end)
    {
        end = null;
        if (!double.TryParse(string.IsNullOrWhiteSpace(CutinStartBox.Text) ? "0" : CutinStartBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out start) || start < 0)
        {
            MessageBox.Show(Loc.T("cutins.bad_trim"), AppCaption);
            return false;
        }
        if (!string.IsNullOrWhiteSpace(CutinEndBox.Text))
        {
            if (!double.TryParse(CutinEndBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var en) || en <= start)
            {
                MessageBox.Show(Loc.T("cutins.bad_trim"), AppCaption);
                return false;
            }
            end = en;
        }
        return true;
    }

    // ------------------------------------------------------------------ preview

    private async void CutinBuildPreview_Click(object sender, RoutedEventArgs e)
    {
        if (CutinCardList.SelectedItem is not CardRecord card)
        {
            MessageBox.Show(Loc.T("cutins.pick_card_first"), AppCaption);
            return;
        }
        var video = CutinVideoPathBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(video) || !File.Exists(video))
        {
            MessageBox.Show(Loc.T("cutins.pick_video_first"), AppCaption);
            return;
        }
        if (!TryReadTrim(out var start, out var end))
            return;

        StopCutinPreview();
        CutinInstallButton.IsEnabled = false;
        var options = new VideoCutinOptions { Start = start, End = end };
        var workRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Floowan", "cutin-work", "app");
        var workDir = Path.Combine(workRoot, $"{card.Id}-{DateTime.Now:yyyyMMdd-HHmmss}");
        var name = card.DisplayName;
        var progress = new Progress<string>(Status);
        SetUiBusy(true);
        Status(Loc.T("cutins.building_preview"));
        try
        {
            var (result, frames) = await Task.Run(() =>
            {
                foreach (var old in Directory.Exists(workRoot) ? Directory.GetDirectories(workRoot) : [])
                {
                    try { Directory.Delete(old, recursive: true); } catch { /* in use */ }
                }
                var r = VideoFlipbook.Build(video, card.Id, name, options, new MediaFoundationVideoReader(), workDir, progress);
                var bmps = r.PreviewFrames.Select(ToBitmap).ToArray();
                foreach (var f in r.PreviewFrames)
                    f.Dispose();
                return (r, bmps);
            });
            _cutinPreviewFrames = frames;
            _cutinPreviewIndex = 0;
            _cutinPreviewDir = result.ContentDir;
            _cutinPreviewCard = card;
            CutinPreviewEmptyText.Visibility = Visibility.Collapsed;
            CutinPlayButton.IsEnabled = true;
            StartCutinPreview();
            var speed = result.ClipSeconds / result.Seconds;
            CutinPreviewInfoText.Text = Loc.T("cutins.preview_info", name, result.Frames, result.FrameWidth, result.FrameHeight,
                result.ClipSeconds.ToString("0.00", CultureInfo.InvariantCulture), result.Seconds.ToString("0.0", CultureInfo.InvariantCulture),
                speed.ToString("0.00", CultureInfo.InvariantCulture), result.HdPage.Width, result.HdPage.Height);
            CutinInstallButton.IsEnabled = true;
            Status(Loc.T("cutins.preview_ready"));
        }
        catch (Exception ex)
        {
            Status(ex.Message);
            MessageBox.Show(Loc.T("cutins.preview_failed", ex.Message), AppCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetUiBusy(false);
        }
    }

    private static BitmapSource ToBitmap(SixLabors.ImageSharp.Image<Rgba32> img)
    {
        var w = img.Width;
        var h = img.Height;
        var bgra = new byte[w * h * 4];
        img.ProcessPixelRows(acc =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = acc.GetRowSpan(y);
                var o = y * w * 4;
                for (var x = 0; x < w; x++, o += 4)
                {
                    bgra[o] = row[x].B;
                    bgra[o + 1] = row[x].G;
                    bgra[o + 2] = row[x].R;
                    bgra[o + 3] = 255;
                }
            }
        });
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private void StartCutinPreview()
    {
        if (!_cutinTimerHooked)
        {
            _cutinTimer.Tick += CutinTimer_Tick;
            _cutinTimerHooked = true;
        }
        if (_cutinPreviewFrames.Length == 0)
            return;
        _cutinTimer.Start();
        CutinPlayButton.Content = Loc.T("cutins.pause");
    }

    private void StopCutinPreview()
    {
        _cutinTimer.Stop();
        CutinPlayButton.Content = Loc.T("cutins.play");
    }

    private void CutinTimer_Tick(object? sender, EventArgs e)
    {
        if (_cutinPreviewFrames.Length == 0)
            return;
        // play the 2 s once, then hold the last (faded-out) frame for half a second before looping
        const int hold = 15;
        CutinPreviewImage.Source = _cutinPreviewFrames[Math.Min(_cutinPreviewIndex, _cutinPreviewFrames.Length - 1)];
        _cutinPreviewIndex = (_cutinPreviewIndex + 1) % (_cutinPreviewFrames.Length + hold);
    }

    private void CutinPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_cutinTimer.IsEnabled)
            StopCutinPreview();
        else
            StartCutinPreview();
    }

    private void CutinShowNameBar_Changed(object sender, RoutedEventArgs e)
    {
        if (CutinNameBarOverlay is not null)
            CutinNameBarOverlay.Visibility = CutinShowNameBarBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ install / remove / restore

    private async Task<ICutinTextureEncoder?> EnsureCutinEncoderAsync()
    {
        try
        {
            var path = await TexconvProvider.EnsureAsync(CutinHttp, new Progress<string>(Status));
            return new TexconvBc7Encoder(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.T("cutins.texconv_failed", ex.Message, TexconvProvider.Url, TexconvProvider.DownloadPath), AppCaption,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private bool RefuseWhileGameRuns()
    {
        if (!CutinInstaller.IsGameRunning())
            return false;
        MessageBox.Show(Loc.T("cutins.close_game"), AppCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    private async void CutinInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_cutinPreviewCard is not { } card || _cutinPreviewDir is null || !Directory.Exists(_cutinPreviewDir))
        {
            MessageBox.Show(Loc.T("cutins.build_preview_first"), AppCaption);
            return;
        }
        if (!TryGetCutinGamePath(out var gamePath) || RefuseWhileGameRuns())
            return;
        var official = _cutinOfficialIds?.Contains(card.Id) == true;
        if (official && CutinReplaceOfficialBox.IsChecked != true)
        {
            MessageBox.Show(Loc.T("cutins.official_needs_confirm", card.DisplayName), AppCaption, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var replacing = CutinRegistry.Load().Get(card.Id) is not null;
        var confirm = MessageBox.Show(
            Loc.T("cutins.install_confirm", card.DisplayName, card.Id) +
            (replacing ? "\n\n" + Loc.T("cutins.install_replaces_custom") : "") +
            (official ? "\n\n" + Loc.T("cutins.install_replaces_official") : ""),
            Loc.T("cutins.install_confirm_title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        SetUiBusy(true);
        try
        {
            var encoder = await EnsureCutinEncoderAsync();
            if (encoder is null)
                return;
            Status(Loc.T("cutins.installing", card.DisplayName));
            var dir = _cutinPreviewDir;
            var progress = new Progress<string>(Status);    // created on the UI thread, so reports run there
            var result = await Task.Run(() => CreateCutinInstaller(gamePath, encoder)
                .RegisterCustom(card.Id, dir, card.DisplayName, CutinNaming.DefaultTemplate, official, progress));
            Status(Loc.T("cutins.installed", card.DisplayName));
            MessageBox.Show(Loc.T("cutins.installed_message", card.DisplayName, result.Active) + MessagesSuffix(result), AppCaption,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Status(ex.Message);
            MessageBox.Show(Loc.T("cutins.install_failed", ex.Message), AppCaption, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetUiBusy(false);
        }
        await RefreshCutinListAsync(quiet: true);
    }

    private static string MessagesSuffix(CutinApplyResult r) =>
        r.Messages.Count == 0 ? "" : "\n\n" + string.Join("\n", r.Messages);

    private List<CutinListItem> SelectedCutinItems() => CutinList.SelectedItems.OfType<CutinListItem>().ToList();

    private async void CutinRemove_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedCutinItems();
        if (items.Count == 0)
        {
            MessageBox.Show(Loc.T("cutins.select_first"), AppCaption);
            return;
        }
        if (!TryGetCutinGamePath(out var gamePath) || RefuseWhileGameRuns())
            return;
        var names = string.Join("\n", items.Select(i => "• " + i.Name));
        if (MessageBox.Show(Loc.T("cutins.remove_confirm", items.Count, names), Loc.T("cutins.remove_confirm_title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        SetUiBusy(true);
        try
        {
            Status(Loc.T("cutins.removing"));
            var ids = items.Select(i => i.CardId).ToList();
            var needsEncoder = CutinRegistry.Load().Entries.Any(x => x.IsCustom && !ids.Contains(x.CardId));
            var encoder = needsEncoder ? await EnsureCutinEncoderAsync() : null;
            var progress = new Progress<string>(Status);
            var r = await Task.Run(() => CreateCutinInstaller(gamePath, encoder).Remove(ids, progress));
            Status(Loc.T("cutins.removed", items.Count));
            MessageBox.Show(Loc.T("cutins.removed_message", items.Count, r.Active) + MessagesSuffix(r), AppCaption);
        }
        catch (Exception ex)
        {
            Status(ex.Message);
            MessageBox.Show(ex.Message, AppCaption, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetUiBusy(false);
        }
        await RefreshCutinListAsync(quiet: true);
    }

    private async void CutinRestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCutinGamePath(out var gamePath) || RefuseWhileGameRuns())
            return;
        SetUiBusy(true);
        try
        {
            var msg = await RestoreCutinsAsync(gamePath);
            if (msg is not null)
                MessageBox.Show(msg, AppCaption);
        }
        finally
        {
            SetUiBusy(false);
        }
        await RefreshCutinListAsync(quiet: true);
    }

    /// <summary>Re-install every registered cut-in (after a game update). Null when there is nothing to do or it failed.</summary>
    private async Task<string?> RestoreCutinsAsync(string gamePath)
    {
        if (CutinRegistry.Load().Entries.Count == 0)
            return Loc.T("cutins.nothing_registered");
        try
        {
            var encoder = await EnsureCutinEncoderAsync();
            if (encoder is null)
                return null;
            Status(Loc.T("cutins.restoring"));
            var progress = new Progress<string>(Status);
            var r = await Task.Run(() => CreateCutinInstaller(gamePath, encoder).ApplyAll(false, progress));
            var msg = Loc.T("cutins.restored", r.Active, r.Built, r.Kept) + MessagesSuffix(r);
            Status(Loc.T("cutins.restored", r.Active, r.Built, r.Kept));
            return msg;
        }
        catch (Exception ex)
        {
            Status(ex.Message);
            return Loc.T("cutins.restore_failed", ex.Message);
        }
    }

    // ------------------------------------------------------------------ packs

    private async void CutinExport_Click(object sender, RoutedEventArgs e)
    {
        var reg = CutinRegistry.Load();
        var ids = SelectedCutinItems().Select(i => i.CardId).ToList();
        if (ids.Count == 0)
            ids = reg.Entries.Select(x => x.CardId).ToList();
        if (ids.Count == 0)
        {
            MessageBox.Show(Loc.T("cutins.nothing_registered"), AppCaption);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = Loc.T("cutins.export_title"),
            Filter = Loc.T("cutins.pack_filter") + "|*" + CutinPack.Extension,
            FileName = ids.Count == 1 ? $"{CutinCardName(ids[0], null)}{CutinPack.Extension}" : $"cutins-{DateTime.Now:yyyyMMdd}{CutinPack.Extension}",
        };
        foreach (var c in Path.GetInvalidFileNameChars())
            dlg.FileName = dlg.FileName.Replace(c, '_');
        if (dlg.ShowDialog(this) != true)
            return;
        SetUiBusy(true);
        try
        {
            Status(Loc.T("cutins.exporting", ids.Count));
            var path = dlg.FileName;
            await Task.Run(() => CutinPack.Export(reg, ids, path));
            Status(Loc.T("cutins.exported", ids.Count, path));
            MessageBox.Show(Loc.T("cutins.exported", ids.Count, path), AppCaption);
        }
        catch (Exception ex)
        {
            Status(ex.Message);
            MessageBox.Show(ex.Message, AppCaption, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetUiBusy(false);
        }
    }

    private async void CutinImport_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCutinGamePath(out var gamePath))
            return;
        var dlg = new OpenFileDialog { Title = Loc.T("cutins.import_title"), Filter = Loc.T("cutins.pack_filter") + "|*" + CutinPack.Extension };
        if (dlg.ShowDialog(this) != true)
            return;
        var pack = dlg.FileName;
        IReadOnlyList<CutinPackCard> cards;
        try
        {
            cards = CutinPack.ReadCards(pack);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var items = cards.Select(c =>
        {
            var record = _database?.GetById(c.CardId);
            return new CardPickDialog.PickItem
            {
                Id = c.CardId,
                Name = record?.DisplayName ?? c.Name,
                BadgeText = c.Payload switch
                {
                    CutinPackPayload.Video => Loc.T("cutins.kind_video"),
                    CutinPackPayload.Borrow => Loc.T("cutins.kind_borrow", c.Source ?? 0),
                    _ => Loc.T("cutins.kind_custom"),
                },
                Label = record?.DisplayName ?? c.Name,
                Bundle = record?.Bundle ?? "",
            };
        }).ToList();
        var pick = new CardPickDialog(this, Loc.T("cutins.import_pick_title"), Loc.T("cutins.import_pick_intro"), items, gamePath);
        if (pick.ShowDialog() != true || pick.SelectedIds.Count == 0)
            return;
        if (RefuseWhileGameRuns())
            return;
        var selected = cards.Where(c => pick.SelectedIds.Contains(c.CardId)).ToList();
        var unknown = selected.Where(c => _database?.GetById(c.CardId) is null).ToList();
        if (unknown.Count > 0)
        {
            MessageBox.Show(Loc.T("cutins.import_unknown_cards", string.Join(", ", unknown.Select(c => c.CardId))), AppCaption);
            selected = selected.Except(unknown).ToList();
        }
        if (selected.Count == 0)
            return;

        SetUiBusy(true);
        try
        {
            var official = _cutinOfficialIds ?? await Task.Run(() => OfficialIds(CreateCutinInstaller(gamePath)));
            var withOfficial = selected.Where(c => official.Contains(c.CardId)).ToList();
            var overrideOfficial = false;
            if (withOfficial.Count > 0)
            {
                var answer = MessageBox.Show(Loc.T("cutins.import_official_confirm", withOfficial.Count,
                        string.Join("\n", withOfficial.Select(c => "• " + CutinCardName(c.CardId, c.Name)))),
                    AppCaption, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel)
                    return;
                overrideOfficial = answer == MessageBoxResult.Yes;
                if (!overrideOfficial)
                    selected = selected.Except(withOfficial).ToList();
            }
            var encoder = await EnsureCutinEncoderAsync();
            if (encoder is null)
                return;
            var progress = new Progress<string>(Status);
            var names = selected.ToDictionary(c => c.CardId, c => CutinCardName(c.CardId, c.Name));
            var r = await Task.Run(() =>
            {
                var inst = CreateCutinInstaller(gamePath, encoder);
                var work = Path.Combine(Path.GetTempPath(), "floowan-cutin-import-" + Guid.NewGuid().ToString("N"));
                try
                {
                    foreach (var c in selected)
                    {
                        var name = names[c.CardId];
                        var ov = overrideOfficial && official.Contains(c.CardId);
                        var dir = Path.Combine(work, c.CardId.ToString(CultureInfo.InvariantCulture));
                        switch (c.Payload)
                        {
                            case CutinPackPayload.Borrow:
                                inst.RegisterBorrow(c.CardId, c.Source!.Value, name, ov, progress, apply: false);
                                break;
                            case CutinPackPayload.Video:
                            {
                                var video = CutinPack.ExtractCard(pack, c, dir)!;
                                var opts = CutinPack.ReadPackVideoOptions(dir);
                                var content = Path.Combine(dir, "content");
                                ((IProgress<string>)progress).Report(Loc.T("cutins.import_building", name));
                                var built = VideoFlipbook.Build(video, c.CardId, name, opts, new MediaFoundationVideoReader(), content, progress);
                                foreach (var f in built.PreviewFrames)
                                    f.Dispose();
                                inst.RegisterCustom(c.CardId, content, name, c.Template ?? CutinNaming.DefaultTemplate, ov, progress, apply: false);
                                break;
                            }
                            default:
                            {
                                CutinPack.ExtractCard(pack, c, dir);
                                var metaText = File.ReadAllText(Path.Combine(dir, "meta.json"));
                                using var meta = System.Text.Json.JsonDocument.Parse(metaText);
                                if (meta.RootElement.TryGetProperty("card_id", out var mid) && mid.GetInt32() != c.CardId)
                                    throw new InvalidDataException($"The pack's content for card {c.CardId} was built for card {mid.GetInt32()}.");
                                inst.RegisterCustom(c.CardId, dir, name, c.Template ?? CutinNaming.DefaultTemplate, ov, progress, apply: false);
                                break;
                            }
                        }
                    }
                    return inst.ApplyAll(false, progress);
                }
                finally
                {
                    try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
                }
            });
            Status(Loc.T("cutins.imported", selected.Count));
            MessageBox.Show(Loc.T("cutins.imported_message", selected.Count, r.Active) + MessagesSuffix(r), AppCaption);
        }
        catch (Exception ex)
        {
            Status(ex.Message);
            MessageBox.Show(Loc.T("cutins.import_failed", ex.Message), AppCaption, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetUiBusy(false);
        }
        await RefreshCutinListAsync(quiet: true);
    }
}
