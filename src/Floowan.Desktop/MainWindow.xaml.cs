using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Floowan.Core.Data;
using Floowan.Core.Game;
using Floowan.Core.Models;
using Floowan.Core.Services;
using Microsoft.Win32;

namespace Floowan.Desktop;

public partial class MainWindow : Window
{
    private CardDatabase? _database;
    private CardArtModService? _modService;
    private CardRecord? _selected;
    private string? _replacementImagePath;
    private string? _previewTempPath;

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
            _modService = new CardArtModService(backupRoot: Path.Combine(AppContext.BaseDirectory, "backups"));

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
                RunSearch();
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
            CreateBackupBox.IsChecked = b;
    }

    private void SetGamePath(string path)
    {
        GamePathBox.Text = path;
        try { _database?.SetStoredGamePath(path); } catch { /* read-only ok */ }
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

    private void Cleanup()
    {
        CleanupPreviewTemp();
        _modService?.Dispose();
        _database?.Dispose();
    }

    private void Status(string text) => StatusText.Text = text;
}
