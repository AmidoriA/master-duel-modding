using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

    private const double ViewportLoadBufferPx = 160;

    private readonly ObservableCollection<PickItem> _items;
    private readonly ObservableCollection<PickItem> _selectedSidebar = new();
    private readonly ICollectionView _view;
    private readonly string? _gamePath;
    private readonly CardThumbnailCache _thumbnailCache;
    private readonly ConcurrentDictionary<int, byte> _inflightLoads = new();
    private CancellationTokenSource _thumbLoadCts = new();
    private bool _visibleLoadPassScheduled;

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

        CardList.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (CardList.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                ScheduleVisibleThumbnailLoads();
        };
        Loaded += (_, _) => ScheduleVisibleThumbnailLoads();
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
        CancelThumbnailLoadsAndReloadVisible();
    }

    private void CardScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0 && e.ExtentHeightChange == 0)
            return;
        ScheduleVisibleThumbnailLoads();
    }

    private void CardScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ScheduleVisibleThumbnailLoads();

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

    private void CancelThumbnailLoadsAndReloadVisible()
    {
        var previous = _thumbLoadCts;
        _thumbLoadCts = new CancellationTokenSource();
        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
            /* ignore */
        }

        previous.Dispose();
        // Leave in-flight ids until their tasks finish; finally re-schedules so still-visible
        // cards can start again under the new token.
        ScheduleVisibleThumbnailLoads();
    }

    private void ScheduleVisibleThumbnailLoads()
    {
        if (_visibleLoadPassScheduled)
            return;

        _visibleLoadPassScheduled = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _visibleLoadPassScheduled = false;
                StartLoadsForViewport();
            },
            DispatcherPriority.Background);
    }

    private void StartLoadsForViewport()
    {
        if (!IsLoaded || CardScrollViewer is null || CardList is null)
            return;

        var token = _thumbLoadCts.Token;
        if (token.IsCancellationRequested)
            return;

        foreach (var item in VisibleItems())
        {
            if (token.IsCancellationRequested)
                return;

            var card = item.AsCardRecord();
            if (_thumbnailCache.TryGet(card, out var cached)
                && !ReferenceEquals(cached, _thumbnailCache.Placeholder))
            {
                if (!ReferenceEquals(item.Thumbnail, cached))
                    item.Thumbnail = cached;
                continue;
            }

            if (!IsItemInScrollViewport(item))
                continue;

            if (!_inflightLoads.TryAdd(item.Id, 0))
                continue;

            _ = LoadThumbnailForItemAsync(item, token);
        }
    }

    private bool IsItemInScrollViewport(PickItem item)
    {
        var container = CardList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
        if (container is null || !container.IsLoaded || container.ActualHeight <= 0)
            return false;

        try
        {
            var transform = container.TransformToAncestor(CardScrollViewer);
            var bounds = transform.TransformBounds(
                new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            var viewport = new Rect(
                0,
                -ViewportLoadBufferPx,
                CardScrollViewer.ViewportWidth,
                CardScrollViewer.ViewportHeight + (2 * ViewportLoadBufferPx));
            return viewport.IntersectsWith(bounds);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task LoadThumbnailForItemAsync(PickItem item, CancellationToken token)
    {
        try
        {
            var loaded = await _thumbnailCache.GetOrLoadAsync(_gamePath, item.AsCardRecord(), token)
                .ConfigureAwait(true);
            if (token.IsCancellationRequested)
                return;
            item.Thumbnail = loaded;
        }
        catch (OperationCanceledException)
        {
            /* superseded by search/scroll cancel */
        }
        catch
        {
            if (!token.IsCancellationRequested)
                item.Thumbnail = _thumbnailCache.Placeholder;
        }
        finally
        {
            _inflightLoads.TryRemove(item.Id, out _);
            if (token.IsCancellationRequested)
                ScheduleVisibleThumbnailLoads();
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
        try
        {
            _thumbLoadCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            /* ignore */
        }

        _thumbLoadCts.Dispose();
        _thumbnailCache.Dispose();
    }
}
