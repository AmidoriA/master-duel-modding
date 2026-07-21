using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Floowan.Core.Assets;
using Floowan.Core.Data;
using Floowan.Core.Game;
using Floowan.Core.Models;
using Floowan.Core.Services;
using Microsoft.Win32;

namespace Floowan.Desktop;

public partial class MainWindow : Window
{
    private const int DbPageSize = 100;

    private CardDatabase? _database;
    private CardArtModService? _modService;
    private OverFrameModService? _overFrameService;
    private CardRecord? _selected;
    private CardRecord? _ofSelected;
    private string? _replacementImagePath;
    private string? _ofReplacementImagePath;
    private string? _previewTempPath;
    private string? _ofPreviewTempPath;
    private bool _ofGateReady;
    private bool _ofInitialScanStarted;

    private CardRecord? _dbSelected;
    private int _dbOffset;
    private int _dbTotalMatching;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => Cleanup();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var backupRoot = Path.Combine(AppContext.BaseDirectory, "backups");
            _modService = new CardArtModService(backupRoot: backupRoot);
            _overFrameService = new OverFrameModService(backupRoot: backupRoot);

            var defaultDb = FindDefaultDatabase();
            if (defaultDb is not null)
                OpenDatabase(defaultDb);

            var discovered = GamePathLocator.FindSteamMasterDuelPaths();
            if (discovered.Count == 1)
            {
                SetGamePath(discovered[0]);
            }
            else if (_database?.GetStoredGamePath() is string stored &&
                     GamePathLocator.IsValidGamePath(stored, out _))
            {
                SetGamePath(stored);
            }

            Status($"Loaded. Cards in DB: {_database?.CountCards() ?? 0}. Discovered installs: {discovered.Count}.");
            if (_database is not null)
            {
                RunSearch();
                RunOfSearch();
                RefreshOfGateStatusFromCache();
                RunDatabaseQuery(resetOffset: true);
            }
        }
        catch (Exception ex)
        {
            Status("Startup error: " + ex.Message);
            MessageBox.Show(ex.Message, "Floowan", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? FindDefaultDatabase()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "database.db"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "database.db")),
            Path.Combine(Directory.GetCurrentDirectory(), "database.db"),
            @"C:\Users\user\Documents\Projects\Floowan-copy\database.db"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private void OpenDatabase(string path)
    {
        _database?.Dispose();
        _database = new CardDatabase(path);
        DatabasePathBox.Text = path;
        var flag = _database.GetCreateBackupFlag();
        if (flag is bool b)
        {
            CreateBackupBox.IsChecked = b;
            OfCreateBackupBox.IsChecked = b;
        }

        RefreshOfGateStatusFromCache();
    }

    private void SetGamePath(string path)
    {
        GamePathBox.Text = path;
        try { _database?.SetStoredGamePath(path); } catch { /* read-only ok */ }
        _ofGateReady = false;
        RefreshOfGateStatusFromCache();
    }

    private void DiscoverSteam_Click(object sender, RoutedEventArgs e)
    {
        var paths = GamePathLocator.FindSteamMasterDuelPaths();
        if (paths.Count == 0)
        {
            MessageBox.Show("No valid Master Duel LocalData folders were found via Steam libraries.", "Floowan");
            return;
        }

        if (paths.Count == 1)
        {
            SetGamePath(paths[0]);
            Status("Selected LocalData: " + paths[0]);
            return;
        }

        var choice = paths[0];
        var dlg = new Window
        {
            Title = "Select Master Duel player data",
            Owner = this,
            Width = 720,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Foreground = Foreground
        };
        var list = new ListBox { Margin = new Thickness(12) };
        foreach (var p in paths)
            list.Items.Add(p);
        list.SelectedIndex = 0;
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(12), IsDefault = true };
        ok.Click += (_, _) => { choice = (string)list.SelectedItem; dlg.DialogResult = true; };
        var panel = new DockPanel();
        DockPanel.SetDock(ok, Dock.Bottom);
        panel.Children.Add(ok);
        panel.Children.Add(list);
        dlg.Content = panel;
        if (dlg.ShowDialog() == true)
        {
            SetGamePath(choice);
            Status("Selected LocalData: " + choice);
        }
    }

    private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select Master Duel LocalData/<playerId> folder" };
        if (dlg.ShowDialog(this) == true)
        {
            if (!GamePathLocator.IsValidGamePath(dlg.FolderName, out var error))
            {
                MessageBox.Show(error ?? "Invalid path", "Floowan", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SetGamePath(dlg.FolderName);
            Status("Selected LocalData: " + dlg.FolderName);
        }
    }

    private void BrowseDatabase_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Floowandereeze database.db",
            Filter = "SQLite DB (*.db)|*.db|All files|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            OpenDatabase(dlg.FileName);
            RunSearch();
            RunOfSearch();
            RunDatabaseQuery(resetOffset: true);
            Status("Opened database: " + dlg.FileName);
        }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            RunSearch();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => RunSearch();

    private void RunSearch()
    {
        if (_database is null)
        {
            Status("Open database.db first.");
            return;
        }

        var results = _database.SearchCards(
            SearchBox.Text,
            favoritesOnly: FavoritesOnlyBox.IsChecked == true,
            searchDescription: SearchDescBox.IsChecked == true,
            limit: 400);
        CardList.ItemsSource = results;
        Status($"Showing {results.Count} card(s).");
    }

    private void CardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = CardList.SelectedItem as CardRecord;
        _replacementImagePath = null;
        ReplacementImage.Source = null;
        ImagePathText.Text = "";
        DetailText.Text = "";

        if (_selected is null)
        {
            CardTitleText.Text = "(none selected)";
            CardMetaText.Text = "";
            CurrentArtImage.Source = null;
            return;
        }

        CardTitleText.Text = _selected.DisplayName;
        CardMetaText.Text = $"Bundle {_selected.Bundle} · id {_selected.Id} · backup={_selected.HasBackup}";
        LoadCurrentPreview();
    }

    private void LoadCurrentPreview()
    {
        if (_selected is null || _modService is null || string.IsNullOrWhiteSpace(GamePathBox.Text))
            return;

        try
        {
            CleanupPreviewTemp();
            _previewTempPath = Path.Combine(Path.GetTempPath(), $"floowan-preview-{Guid.NewGuid():N}.png");
            _modService.ExtractCardArt(GamePathBox.Text, _selected, _previewTempPath);
            CurrentArtImage.Source = LoadBitmap(_previewTempPath);
            var info = _modService.GetTextureInfo(GamePathBox.Text, _selected);
            DetailText.Text = $"Texture '{info.Name}' {info.Width}x{info.Height} format={info.Format} mips={info.MipCount}";
        }
        catch (Exception ex)
        {
            CurrentArtImage.Source = null;
            DetailText.Text = "Preview failed: " + ex.Message;
        }
    }

    private void SelectImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select replacement card art",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        _replacementImagePath = dlg.FileName;
        ImagePathText.Text = "Replacement: " + dlg.FileName;
        ReplacementImage.Source = LoadBitmap(dlg.FileName);
        ReplacementImage.Opacity = 1.0;
        CurrentArtImage.Opacity = 0.35;
    }

    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (_modService is null || _selected is null)
        {
            MessageBox.Show("Select a card first.", "Floowan");
            return;
        }
        if (string.IsNullOrWhiteSpace(GamePathBox.Text))
        {
            MessageBox.Show("Set the Master Duel LocalData path first.", "Floowan");
            return;
        }
        if (string.IsNullOrWhiteSpace(_replacementImagePath))
        {
            MessageBox.Show("Select a replacement image first.", "Floowan");
            return;
        }

        var createBackup = CreateBackupBox.IsChecked == true;
        var gamePath = GamePathBox.Text;
        var card = _selected;
        var image = _replacementImagePath;
        IsEnabled = false;
        Status("Replacing card art…");
        try
        {
            var result = await Task.Run(() =>
                _modService.ReplaceCardArt(gamePath, card, image, createBackup, _database));
            Status(result.Message);
            MessageBox.Show(result.Message, "Floowan",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            if (result.Success)
            {
                CurrentArtImage.Opacity = 1.0;
                ReplacementImage.Opacity = 0.0;
                LoadCurrentPreview();
            }
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_modService is null || _selected is null || string.IsNullOrWhiteSpace(GamePathBox.Text))
            return;

        var dlg = new SaveFileDialog
        {
            Title = "Extract card art",
            Filter = "PNG|*.png",
            FileName = _selected.Bundle + ".png"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            _modService.ExtractCardArt(GamePathBox.Text, _selected, dlg.FileName);
            Status("Extracted to " + dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Floowan", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_modService is null || _selected is null || string.IsNullOrWhiteSpace(GamePathBox.Text))
            return;

        try
        {
            var ok = _modService.RestoreCardArt(GamePathBox.Text, _selected, _database);
            Status(ok ? "Restored from backup." : "No bundle backup found for this card.");
            if (ok)
                LoadCurrentPreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Floowan", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Over-frame tab ---

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || OverFrameTab is null)
            return;
        if (!ReferenceEquals(MainTabs.SelectedItem, OverFrameTab))
            return;
        if (_ofInitialScanStarted || _ofGateReady)
            return;
        if (string.IsNullOrWhiteSpace(GamePathBox.Text) || _overFrameService is null)
            return;

        _ofInitialScanStarted = true;
        _ = EnsureOfGateAsync(showErrors: false);
    }

    private void OfSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            RunOfSearch();
    }

    private void OfSearch_Click(object sender, RoutedEventArgs e) => RunOfSearch();

    private void OfFilter_Changed(object sender, RoutedEventArgs e) => RunOfSearch();

    private void RunOfSearch()
    {
        if (_database is null)
        {
            Status("Open database.db first.");
            return;
        }

        var results = _database.SearchCards(
            OfSearchBox.Text,
            favoritesOnly: OfFavoritesOnlyBox.IsChecked == true,
            overframeOnly: OfOverframeOnlyBox.IsChecked == true,
            limit: 400);
        OfCardList.ItemsSource = results;
        Status($"Over-frame tab: {results.Count} card(s).");
    }

    private void OfCardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ofSelected = OfCardList.SelectedItem as CardRecord;
        _ofReplacementImagePath = null;
        OfReplacementImage.Source = null;
        OfImagePathText.Text = "";
        OfCurrentArtImage.Opacity = 1.0;

        if (_ofSelected is null)
        {
            OfCardTitleText.Text = "(none selected)";
            OfCardMetaText.Text = "";
            OfGateEntryText.Text = "";
            OfCurrentArtImage.Source = null;
            OfDetailText.Text = "Target texture size: 704×1024 (RGBA32). Tip: keep alpha ≈ 4 for foil mask areas.";
            return;
        }

        OfCardTitleText.Text = _ofSelected.DisplayName;
        OfCardMetaText.Text =
            $"Bundle {_ofSelected.Bundle} · id {_ofSelected.Id} · overframe={_ofSelected.IsOverframe}" +
            (_ofSelected.OverframeBaseId is int baseId ? $" · base={baseId}" : "");
        RefreshOfGateEntryStatus();
        LoadOfCurrentPreview();
    }

    private void RefreshOfGateEntryStatus()
    {
        if (_ofSelected is null || _overFrameService is null || string.IsNullOrWhiteSpace(GamePathBox.Text) || !_ofGateReady)
        {
            OfGateEntryText.Text = _ofGateReady ? "" : "Gate not located yet — open this tab or click Scan.";
            return;
        }

        try
        {
            var inGate = _overFrameService.IsInGate(GamePathBox.Text, _ofSelected.Id, _database);
            OfGateEntryText.Text = inGate
                ? $"Gate: present as ({_ofSelected.Id},{_ofSelected.Id})"
                : "Gate: not registered";
        }
        catch (Exception ex)
        {
            OfGateEntryText.Text = "Gate check failed: " + ex.Message;
        }
    }

    private void RefreshOfGateStatusFromCache()
    {
        var cached = _database?.GetOfCardAssetBundleId();
        if (!string.IsNullOrWhiteSpace(cached))
        {
            OfGateStatusText.Text = $"Gate bundle (cached): {cached}";
            _ofGateReady = true;
        }
        else
        {
            OfGateStatusText.Text = "Gate: not scanned yet";
            _ofGateReady = false;
        }
    }

    private async void OfScanGate_Click(object sender, RoutedEventArgs e) =>
        await EnsureOfGateAsync(showErrors: true);

    private async Task EnsureOfGateAsync(bool showErrors)
    {
        if (_overFrameService is null)
            return;
        if (string.IsNullOrWhiteSpace(GamePathBox.Text))
        {
            if (showErrors)
                MessageBox.Show("Set the Master Duel LocalData path first.", "Floowan");
            return;
        }

        IsEnabled = false;
        OfGateStatusText.Text = "Scanning for of_card_asset…";
        Status("Scanning for of_card_asset…");
        var gamePath = GamePathBox.Text;
        var progress = new Progress<string>(msg =>
        {
            OfGateStatusText.Text = msg;
            Status(msg);
        });

        try
        {
            var result = await Task.Run(() =>
                _overFrameService.EnsureGateLocated(gamePath, _database, progress));
            if (result.Success)
            {
                _ofGateReady = true;
                OfGateStatusText.Text = result.Message + (result.BundleId is null ? "" : $" ({result.BundleId})");
                Status(result.Message);
                if (_database is not null)
                {
                    try
                    {
                        var marked = _overFrameService.SyncDatabaseFromGate(gamePath, _database);
                        Status($"{result.Message} Synced {marked} over-frame flag(s).");
                        RunOfSearch();
                    }
                    catch (Exception syncEx)
                    {
                        Status(result.Message + " Sync skipped: " + syncEx.Message);
                    }
                }

                RefreshOfGateEntryStatus();
            }
            else
            {
                _ofGateReady = false;
                OfGateStatusText.Text = result.Message;
                Status(result.Message);
                if (showErrors)
                    MessageBox.Show(result.Message, "Floowan", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _ofGateReady = false;
            OfGateStatusText.Text = "Scan failed: " + ex.Message;
            Status(OfGateStatusText.Text);
            if (showErrors)
                MessageBox.Show(ex.Message, "Floowan", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void LoadOfCurrentPreview()
    {
        if (_ofSelected is null || _overFrameService is null || string.IsNullOrWhiteSpace(GamePathBox.Text))
            return;

        try
        {
            CleanupOfPreviewTemp();
            _ofPreviewTempPath = Path.Combine(Path.GetTempPath(), $"floowan-of-preview-{Guid.NewGuid():N}.png");
            _overFrameService.ExtractCardArt(GamePathBox.Text, _ofSelected, _ofPreviewTempPath);
            OfCurrentArtImage.Source = LoadBitmap(_ofPreviewTempPath);
            var info = _overFrameService.GetTextureInfo(GamePathBox.Text, _ofSelected);
            OfDetailText.Text =
                $"Texture '{info.Name}' {info.Width}x{info.Height} format={info.Format}. Target over-frame: {OverFrameConstants.Width}x{OverFrameConstants.Height} RGBA32.";
        }
        catch (Exception ex)
        {
            OfCurrentArtImage.Source = null;
            OfDetailText.Text = "Preview failed: " + ex.Message;
        }
    }

    private void OfSelectImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select 704×1024 over-frame art",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        _ofReplacementImagePath = dlg.FileName;
        OfImagePathText.Text = "Replacement (prefer 704×1024): " + dlg.FileName;
        OfReplacementImage.Source = LoadBitmap(dlg.FileName);
        OfReplacementImage.Opacity = 1.0;
        OfCurrentArtImage.Opacity = 0.35;
    }

    private async void OfApply_Click(object sender, RoutedEventArgs e)
    {
        if (_overFrameService is null || _ofSelected is null)
        {
            MessageBox.Show("Select a card first.", "Floowan");
            return;
        }
        if (string.IsNullOrWhiteSpace(GamePathBox.Text))
        {
            MessageBox.Show("Set the Master Duel LocalData path first.", "Floowan");
            return;
        }
        if (string.IsNullOrWhiteSpace(_ofReplacementImagePath))
        {
            MessageBox.Show("Select a 704×1024 replacement image first.", "Floowan");
            return;
        }

        if (!_ofGateReady)
            await EnsureOfGateAsync(showErrors: true);
        if (!_ofGateReady)
            return;

        var createBackup = OfCreateBackupBox.IsChecked == true;
        var gamePath = GamePathBox.Text;
        var card = _ofSelected;
        var image = _ofReplacementImagePath;
        IsEnabled = false;
        Status("Applying over-frame…");
        try
        {
            var result = await Task.Run(() =>
                _overFrameService.ApplyOverFrame(gamePath, card, image, createBackup, _database));
            Status(result.Message);
            MessageBox.Show(result.Message, "Floowan",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            if (result.Success)
            {
                OfCurrentArtImage.Opacity = 1.0;
                OfReplacementImage.Opacity = 0.0;
                RunOfSearch();
                ReselectOfCard(card.Id);
            }
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void OfEnableGate_Click(object sender, RoutedEventArgs e)
    {
        if (_overFrameService is null || _ofSelected is null)
        {
            MessageBox.Show("Select a card first.", "Floowan");
            return;
        }
        if (string.IsNullOrWhiteSpace(GamePathBox.Text))
        {
            MessageBox.Show("Set the Master Duel LocalData path first.", "Floowan");
            return;
        }

        if (!_ofGateReady)
            await EnsureOfGateAsync(showErrors: true);
        if (!_ofGateReady)
            return;

        var createBackup = OfCreateBackupBox.IsChecked == true;
        var gamePath = GamePathBox.Text;
        var card = _ofSelected;
        IsEnabled = false;
        Status("Updating of_card_asset gate…");
        try
        {
            var result = await Task.Run(() =>
                _overFrameService.EnableGateOnly(gamePath, card, createBackup, _database));
            Status(result.Message);
            MessageBox.Show(result.Message, "Floowan",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            if (result.Success)
            {
                RunOfSearch();
                ReselectOfCard(card.Id);
            }
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void OfRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_overFrameService is null || _ofSelected is null)
        {
            MessageBox.Show("Select a card first.", "Floowan");
            return;
        }
        if (string.IsNullOrWhiteSpace(GamePathBox.Text))
        {
            MessageBox.Show("Set the Master Duel LocalData path first.", "Floowan");
            return;
        }

        if (!_ofGateReady)
            await EnsureOfGateAsync(showErrors: true);
        if (!_ofGateReady)
            return;

        var createBackup = OfCreateBackupBox.IsChecked == true;
        var gamePath = GamePathBox.Text;
        var card = _ofSelected;
        IsEnabled = false;
        Status("Removing from of_card_asset…");
        try
        {
            var result = await Task.Run(() =>
                _overFrameService.RemoveFromGate(gamePath, card, createBackup, _database));
            Status(result.Message);
            MessageBox.Show(result.Message, "Floowan",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            if (result.Success)
            {
                RunOfSearch();
                ReselectOfCard(card.Id);
            }
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void OfRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_overFrameService is null || _ofSelected is null || string.IsNullOrWhiteSpace(GamePathBox.Text))
            return;

        if (!_ofGateReady)
            await EnsureOfGateAsync(showErrors: true);

        var gamePath = GamePathBox.Text;
        var card = _ofSelected;
        IsEnabled = false;
        Status("Restoring over-frame backups…");
        try
        {
            var result = await Task.Run(() =>
                _overFrameService.RestoreBackups(gamePath, card, _database));
            Status(result.Message);
            MessageBox.Show(result.Message, "Floowan",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (result.Success)
            {
                RunOfSearch();
                ReselectOfCard(card.Id);
            }
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void ReselectOfCard(int cardId)
    {
        if (OfCardList.ItemsSource is IEnumerable<CardRecord> items)
        {
            var match = items.FirstOrDefault(c => c.Id == cardId);
            if (match is not null)
            {
                OfCardList.SelectedItem = match;
                return;
            }
        }

        _ofSelected = _database?.GetById(cardId);
        if (_ofSelected is not null)
        {
            OfCardTitleText.Text = _ofSelected.DisplayName;
            OfCardMetaText.Text =
                $"Bundle {_ofSelected.Bundle} · id {_ofSelected.Id} · overframe={_ofSelected.IsOverframe}";
            RefreshOfGateEntryStatus();
            LoadOfCurrentPreview();
        }
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void CleanupPreviewTemp()
    {
        if (_previewTempPath is not null && File.Exists(_previewTempPath))
        {
            try { File.Delete(_previewTempPath); } catch { /* ignore */ }
        }
        _previewTempPath = null;
    }

    private void CleanupOfPreviewTemp()
    {
        if (_ofPreviewTempPath is not null && File.Exists(_ofPreviewTempPath))
        {
            try { File.Delete(_ofPreviewTempPath); } catch { /* ignore */ }
        }
        _ofPreviewTempPath = null;
    }

    private void Cleanup()
    {
        CleanupPreviewTemp();
        CleanupOfPreviewTemp();
        _modService?.Dispose();
        _overFrameService?.Dispose();
        _database?.Dispose();
    }

    private void Status(string text) => StatusText.Text = text;

    private void DbFilter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            RunDatabaseQuery(resetOffset: true);
    }

    private void DbApplyFilters_Click(object sender, RoutedEventArgs e) =>
        RunDatabaseQuery(resetOffset: true);

    private void DbClearFilters_Click(object sender, RoutedEventArgs e)
    {
        DbFilterIdBox.Text = "";
        DbFilterNameBox.Text = "";
        DbFilterDescBox.Text = "";
        DbFilterFavoriteBox.SelectedIndex = 0;
        DbFilterBackupBox.SelectedIndex = 0;
        DbFilterModdedNameBox.SelectedIndex = 0;
        DbFilterModdedDescBox.SelectedIndex = 0;
        RunDatabaseQuery(resetOffset: true);
    }

    private void DbPrevPage_Click(object sender, RoutedEventArgs e)
    {
        _dbOffset = Math.Max(0, _dbOffset - DbPageSize);
        RunDatabaseQuery(resetOffset: false);
    }

    private void DbNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_dbOffset + DbPageSize < _dbTotalMatching)
            _dbOffset += DbPageSize;
        RunDatabaseQuery(resetOffset: false);
    }

    private void RunDatabaseQuery(bool resetOffset)
    {
        if (_database is null)
        {
            Status("Open database.db first.");
            return;
        }

        if (resetOffset)
            _dbOffset = 0;

        try
        {
            var filters = BuildDatabaseFilters();
            _dbTotalMatching = _database.CountCards(filters);
            var page = _database.QueryCards(filters);

            var keepId = _dbSelected?.Id;
            DbCardGrid.ItemsSource = page;

            if (keepId is int id)
            {
                var match = page.FirstOrDefault(c => c.Id == id);
                if (match is not null)
                    DbCardGrid.SelectedItem = match;
                else
                    ClearDatabaseEditForm();
            }
            else if (_dbSelected is null)
            {
                ClearDatabaseEditForm();
            }

            var pageStart = _dbTotalMatching == 0 ? 0 : _dbOffset + 1;
            var pageEnd = Math.Min(_dbOffset + page.Count, _dbTotalMatching);
            DbPageInfoText.Text = $"Showing {pageStart}–{pageEnd} of {_dbTotalMatching}";
            DbPrevPageButton.IsEnabled = _dbOffset > 0;
            DbNextPageButton.IsEnabled = _dbOffset + DbPageSize < _dbTotalMatching;
            Status($"Database: {page.Count} row(s) on page ({_dbTotalMatching} match filter).");
        }
        catch (Exception ex)
        {
            Status("Database query error: " + ex.Message);
        }
    }

    private CardQueryFilters BuildDatabaseFilters()
    {
        int? cardId = null;
        var idText = DbFilterIdBox.Text?.Trim() ?? "";
        if (idText.Length > 0)
        {
            if (!int.TryParse(idText, out var parsed) || parsed <= 0)
                throw new InvalidOperationException("Card ID filter must be a positive integer.");
            cardId = parsed;
        }

        return new CardQueryFilters
        {
            CardId = cardId,
            NameContains = NullIfBlank(DbFilterNameBox.Text),
            DescriptionContains = NullIfBlank(DbFilterDescBox.Text),
            Favorite = TriStateBool(DbFilterFavoriteBox.SelectedIndex),
            HasBackup = TriStateBool(DbFilterBackupBox.SelectedIndex),
            HasModdedName = TriStateBool(DbFilterModdedNameBox.SelectedIndex),
            HasModdedDescription = TriStateBool(DbFilterModdedDescBox.SelectedIndex),
            Limit = DbPageSize,
            Offset = _dbOffset
        };
    }

    private static string? NullIfBlank(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool? TriStateBool(int selectedIndex) => selectedIndex switch
    {
        1 => true,
        2 => false,
        _ => null
    };

    private void DbCardGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _dbSelected = DbCardGrid.SelectedItem as CardRecord;
        if (_dbSelected is null)
        {
            ClearDatabaseEditForm();
            return;
        }

        LoadDatabaseEditForm(_dbSelected);
    }

    private void LoadDatabaseEditForm(CardRecord card)
    {
        DbEditIdBox.Text = card.Id.ToString();
        DbEditNameBox.Text = card.Name;
        DbEditDescBox.Text = card.Description;
        DbEditModdedNameBox.Text = card.ModdedName ?? "";
        DbEditModdedDescBox.Text = card.ModdedDescription ?? "";
        DbEditBundleBox.Text = card.Bundle;
        DbEditDataIndexBox.Text = card.DataIndex.ToString();
        DbEditFavoriteBox.IsChecked = card.Favorite;
        DbEditHasBackupBox.Text = card.HasBackup ? "True" : "False";
    }

    private void ClearDatabaseEditForm()
    {
        _dbSelected = null;
        DbEditIdBox.Text = "";
        DbEditNameBox.Text = "";
        DbEditDescBox.Text = "";
        DbEditModdedNameBox.Text = "";
        DbEditModdedDescBox.Text = "";
        DbEditBundleBox.Text = "";
        DbEditDataIndexBox.Text = "";
        DbEditFavoriteBox.IsChecked = false;
        DbEditHasBackupBox.Text = "";
    }

    private void DbDiscard_Click(object sender, RoutedEventArgs e)
    {
        if (_dbSelected is null)
        {
            ClearDatabaseEditForm();
            return;
        }

        LoadDatabaseEditForm(_dbSelected);
        Status("Discarded Database edit changes.");
    }

    private void DbSave_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null)
        {
            Status("Open database.db first.");
            return;
        }

        if (_dbSelected is null || !int.TryParse(DbEditIdBox.Text, out var id))
        {
            Status("Select a Database row to edit.");
            return;
        }

        try
        {
            _database.UpdateCard(
                id,
                DbEditNameBox.Text ?? "",
                DbEditDescBox.Text ?? "",
                NullIfBlank(DbEditModdedNameBox.Text),
                NullIfBlank(DbEditModdedDescBox.Text),
                DbEditFavoriteBox.IsChecked == true);

            RunDatabaseQuery(resetOffset: false);
            Status($"Saved card id {id}.");
        }
        catch (Exception ex)
        {
            Status("Database save error: " + ex.Message);
        }
    }
}
