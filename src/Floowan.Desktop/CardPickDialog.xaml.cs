using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Floowan.Core.Models;
using Floowan.Desktop.Localization;

namespace Floowan.Desktop;

public partial class CardPickDialog : Window
{
    public sealed class PickItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public int Id { get; init; }

        /// <summary>Card name used for display and case-insensitive search.</summary>
        public string Name { get; init; } = "";

        /// <summary>Optional badge line under the name (e.g. art, layers).</summary>
        public string BadgeText { get; init; } = "";

        /// <summary>Full label (name + badges) for tooltips / legacy callers.</summary>
        public string Label { get; init; } = "";

        /// <summary>Illustration AssetBundle id for thumbnail loading.</summary>
        public string Bundle { get; init; } = "";

        public bool IsOverframe { get; init; }

        public bool HasBadgeText => !string.IsNullOrWhiteSpace(BadgeText);

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal CardRecord AsCardRecord() => new()
        {
            Id = Id,
            Name = Name,
            Bundle = Bundle ?? "",
            IsOverframe = IsOverframe
        };

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly ObservableCollection<PickItem> _items;
    private readonly ICollectionView _view;
    private readonly string? _gamePath;
    private readonly CardThumbnailCache _thumbnailCache;
    private int _thumbnailLoadGeneration;

    public CardPickDialog(
        Window owner,
        string title,
        string intro,
        IEnumerable<PickItem> items,
        string? gamePath = null)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        IntroText.Text = intro;
        _gamePath = gamePath;
        _thumbnailCache = new CardThumbnailCache();

        _items = new ObservableCollection<PickItem>(items);
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterItem;
        CardList.ItemsSource = _view;
        foreach (var item in _items)
            item.PropertyChanged += (_, _) => RefreshCount();

        OkButton.Content = Loc.T("common.confirm");
        CancelButton.Content = Loc.T("common.cancel");
        SelectAllButton.Content = Loc.T("import_export.select_all");
        SelectNoneButton.Content = Loc.T("import_export.select_none");
        SearchLabel.Text = Loc.T("common.search");
        RefreshCount();
    }

    public IReadOnlyList<int> SelectedIds =>
        _items.Where(i => i.IsSelected).Select(i => i.Id).ToList();

    private bool FilterItem(object obj)
    {
        if (obj is not PickItem item)
            return false;

        var query = SearchBox?.Text?.Trim() ?? "";
        if (query.Length == 0)
            return true;

        return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        Interlocked.Increment(ref _thumbnailLoadGeneration);
        RefreshCount();
    }

    private void RefreshCount()
    {
        var selected = _items.Count(i => i.IsSelected);
        CountText.Text = Loc.T("import_export.selected_count", selected, _items.Count);
    }

    private IEnumerable<PickItem> VisibleItems()
    {
        foreach (var obj in _view)
        {
            if (obj is PickItem item)
                yield return item;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in VisibleItems())
            item.IsSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in VisibleItems())
            item.IsSelected = false;
    }

    private void CardCell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PickItem item })
            return;

        item.IsSelected = !item.IsSelected;
        e.Handled = true;
    }

    private async void CardThumbnailImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not PickItem pick)
            return;

        var card = pick.AsCardRecord();
        var generation = _thumbnailLoadGeneration;
        if (_thumbnailCache.TryGet(card, out var cached) && !ReferenceEquals(cached, _thumbnailCache.Placeholder))
        {
            image.Source = cached;
            return;
        }

        image.Source = _thumbnailCache.Placeholder;

        try
        {
            var loaded = await _thumbnailCache.GetOrLoadAsync(_gamePath, card);
            if (generation != _thumbnailLoadGeneration)
                return;
            if (!image.IsLoaded || image.DataContext is not PickItem current || current.Id != pick.Id)
                return;
            image.Source = loaded;
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }
        catch
        {
            if (image.IsLoaded)
                image.Source = _thumbnailCache.Placeholder;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIds.Count == 0)
        {
            MessageBox.Show(
                this,
                Loc.T("import_export.select_at_least_one"),
                Loc.T("app.caption"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _thumbnailCache.Dispose();
    }
}
