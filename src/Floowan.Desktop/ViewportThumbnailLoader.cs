using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Floowan.Core.Models;

namespace Floowan.Desktop;

/// <summary>
/// Loads card thumbnails only for items intersecting the scroll viewport (plus a small buffer).
/// Cancelling the load token stops in-flight extracts when the visible set changes (search/filter).
/// </summary>
internal sealed class ViewportThumbnailLoader<TItem> : IDisposable
    where TItem : class
{
    public const double DefaultBufferPx = 160;

    private readonly CardThumbnailCache _cache;
    private readonly Func<string?> _getGamePath;
    private readonly Func<TItem, int> _getId;
    private readonly Func<TItem, CardRecord> _toCard;
    private readonly Action<TItem, ImageSource> _applyThumbnail;
    private readonly Func<bool>? _isEnabled;
    private readonly double _bufferPx;
    private readonly ConcurrentDictionary<int, byte> _inflightLoads = new();
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource _cts = new();
    private ItemsControl? _itemsControl;
    private ScrollViewer? _scrollViewer;
    private Func<IEnumerable<TItem>>? _enumerateItems;
    private bool _visibleLoadPassScheduled;
    private bool _disposed;

    public ViewportThumbnailLoader(
        CardThumbnailCache cache,
        Func<string?> getGamePath,
        Func<TItem, int> getId,
        Func<TItem, CardRecord> toCard,
        Action<TItem, ImageSource> applyThumbnail,
        Func<bool>? isEnabled = null,
        double bufferPx = DefaultBufferPx,
        Dispatcher? dispatcher = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _getGamePath = getGamePath ?? throw new ArgumentNullException(nameof(getGamePath));
        _getId = getId ?? throw new ArgumentNullException(nameof(getId));
        _toCard = toCard ?? throw new ArgumentNullException(nameof(toCard));
        _applyThumbnail = applyThumbnail ?? throw new ArgumentNullException(nameof(applyThumbnail));
        _isEnabled = isEnabled;
        _bufferPx = bufferPx;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    /// Wire scroll / container generation. Safe to call again to rebind (e.g. after template change).
    /// </summary>
    public void Attach(
        ItemsControl itemsControl,
        ScrollViewer scrollViewer,
        Func<IEnumerable<TItem>> enumerateItems)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DetachHandlers();

        _itemsControl = itemsControl ?? throw new ArgumentNullException(nameof(itemsControl));
        _scrollViewer = scrollViewer ?? throw new ArgumentNullException(nameof(scrollViewer));
        _enumerateItems = enumerateItems ?? throw new ArgumentNullException(nameof(enumerateItems));

        _scrollViewer.ScrollChanged += OnScrollChanged;
        _scrollViewer.SizeChanged += OnSizeChanged;
        _itemsControl.ItemContainerGenerator.StatusChanged += OnGeneratorStatusChanged;
    }

    public void CancelAndReloadVisible()
    {
        if (_disposed)
            return;

        var previous = _cts;
        _cts = new CancellationTokenSource();
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
        ScheduleVisibleLoads();
    }

    public void ScheduleVisibleLoads()
    {
        if (_disposed || _visibleLoadPassScheduled)
            return;

        _visibleLoadPassScheduled = true;
        _dispatcher.BeginInvoke(
            () =>
            {
                _visibleLoadPassScheduled = false;
                if (!_disposed)
                    StartLoadsForViewport();
            },
            DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DetachHandlers();
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            /* ignore */
        }

        _cts.Dispose();
        _itemsControl = null;
        _scrollViewer = null;
        _enumerateItems = null;
    }

    private void DetachHandlers()
    {
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer.SizeChanged -= OnSizeChanged;
        }

        if (_itemsControl is not null)
            _itemsControl.ItemContainerGenerator.StatusChanged -= OnGeneratorStatusChanged;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0 && e.ExtentHeightChange == 0)
            return;
        ScheduleVisibleLoads();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleVisibleLoads();

    private void OnGeneratorStatusChanged(object? sender, EventArgs e)
    {
        if (_itemsControl?.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
            ScheduleVisibleLoads();
    }

    private void StartLoadsForViewport()
    {
        if (_isEnabled is not null && !_isEnabled())
            return;
        if (_itemsControl is null || _scrollViewer is null || _enumerateItems is null)
            return;
        if (!_itemsControl.IsLoaded || !_scrollViewer.IsLoaded)
            return;

        var token = _cts.Token;
        if (token.IsCancellationRequested)
            return;

        foreach (var item in _enumerateItems())
        {
            if (token.IsCancellationRequested)
                return;

            var card = _toCard(item);
            if (_cache.TryGet(card, out var cached)
                && !ReferenceEquals(cached, _cache.Placeholder))
            {
                _applyThumbnail(item, cached);
                continue;
            }

            if (!IsItemInScrollViewport(item))
                continue;

            var id = _getId(item);
            if (!_inflightLoads.TryAdd(id, 0))
                continue;

            _applyThumbnail(item, _cache.Placeholder);
            _ = LoadThumbnailForItemAsync(item, card, id, token);
        }
    }

    private bool IsItemInScrollViewport(TItem item)
    {
        if (_itemsControl is null || _scrollViewer is null)
            return false;

        var container = _itemsControl.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
        if (container is null || !container.IsLoaded || container.ActualHeight <= 0)
            return false;

        try
        {
            var transform = container.TransformToAncestor(_scrollViewer);
            var bounds = transform.TransformBounds(
                new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            var viewport = new Rect(
                0,
                -_bufferPx,
                _scrollViewer.ViewportWidth,
                _scrollViewer.ViewportHeight + (2 * _bufferPx));
            return viewport.IntersectsWith(bounds);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task LoadThumbnailForItemAsync(
        TItem item,
        CardRecord card,
        int id,
        CancellationToken token)
    {
        try
        {
            var loaded = await _cache.GetOrLoadAsync(_getGamePath(), card, token)
                .ConfigureAwait(true);
            if (token.IsCancellationRequested)
                return;
            _applyThumbnail(item, loaded);
        }
        catch (OperationCanceledException)
        {
            /* superseded by search/scroll cancel */
        }
        catch
        {
            if (!token.IsCancellationRequested)
                _applyThumbnail(item, _cache.Placeholder);
        }
        finally
        {
            _inflightLoads.TryRemove(id, out _);
            if (token.IsCancellationRequested)
                ScheduleVisibleLoads();
        }
    }

    /// <summary>Finds the first descendant <see cref="ScrollViewer"/> (e.g. inside a ListBox template).</summary>
    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
            return sv;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>Finds the first descendant <see cref="Image"/> (thumbnail slot in an item template).</summary>
    public static Image? FindDescendantImage(DependencyObject root)
    {
        if (root is Image image)
            return image;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendantImage(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }
}
