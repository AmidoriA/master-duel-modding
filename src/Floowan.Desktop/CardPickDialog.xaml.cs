using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Floowan.Core.Models;
using Floowan.Desktop.Localization;

namespace Floowan.Desktop;

public partial class CardPickDialog : Window
{
    public sealed class PickItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private ImageSource? _thumbnail;

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

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value)) return;
                _thumbnail = value;
                OnPropertyChanged();
            }
        }

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
    private readonly ObservableCollection<PickItem> _selectedSidebar = new();
    private readonly ICollectionView _view;
    private readonly string? _gamePath;
    private readonly CardThumbnailCache _thumbnailCache;
    private readonly ViewportThumbnailLoader<PickItem> _thumbLoader;

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
        _thumbLoader = new ViewportThumbnailLoader<PickItem>(
            _thumbnailCache,
            () => _gamePath,
            item => item.Id,
            item => item.AsCardRecord(),
            (item, src) => item.Thumbnail = src,
            dispatcher: Dispatcher);

        _items = new ObservableCollection<PickItem>(items);
        foreach (var item in _items)
        {
            // Always open with an empty selection; callers may pass IsSelected=true.
            item.IsSelected = false;
            item.Thumbnail = _thumbnailCache.Placeholder;
            item.PropertyChanged += OnPickItemPropertyChanged;
        }

        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterItem;
        CardList.ItemsSource = _view;
        SelectedSidebarList.ItemsSource = _selectedSidebar;

        OkButton.Content = Loc.T("common.confirm");
        CancelButton.Content = Loc.T("common.cancel");
        SelectAllButton.Content = Loc.T("import_export.select_all");
        SelectNoneButton.Content = Loc.T("import_export.select_none");
        SearchLabel.Text = Loc.T("common.search");
        SelectedSidebarHeader.Text = Loc.T("import_export.selected_sidebar");
        SelectedSidebarEmpty.Text = Loc.T("import_export.selected_sidebar_empty");
        RefreshSelectionUi();

        _thumbLoader.Attach(CardList, CardScrollViewer, VisibleItems);
        Loaded += (_, _) => _thumbLoader.ScheduleVisibleLoads();
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
        _thumbLoader.CancelAndReloadVisible();
    }

    private void OnPickItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(PickItem.IsSelected))
            RefreshSelectionUi();
    }

    private void RefreshSelectionUi()
    {
        var selected = _items
            .Where(i => i.IsSelected)
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _selectedSidebar.Clear();
        foreach (var item in selected)
            _selectedSidebar.Add(item);

        var count = selected.Count;
        CountText.Text = Loc.T("import_export.selected_count", count, _items.Count);
        SelectedSidebarEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void SelectedSidebarList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)
            is not { DataContext: PickItem item })
            return;

        ScrollFilteredCardIntoView(item);
    }

    /// <summary>
    /// Scrolls the main thumbnail grid to <paramref name="item"/> when it passes the current
    /// search filter. Quiet no-op when the card is filtered out of the visible set.
    /// </summary>
    private void ScrollFilteredCardIntoView(PickItem item)
    {
        if (!FilterItem(item))
            return;

        CardList.UpdateLayout();
        CardScrollViewer.UpdateLayout();

        if (CardList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
            return;

        container.BringIntoView();
        _thumbLoader.ScheduleVisibleLoads();
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
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
        _thumbLoader.Dispose();
        _thumbnailCache.Dispose();
    }
}
