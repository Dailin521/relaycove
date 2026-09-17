using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RelayCove.App.Controls;
using System.Windows.Input;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class StickerInfiniteScrollBehavior : PlatformBehavior<CollectionView, GridView>
{
    private readonly StickerPagingPolicy _paging = new();
    private CollectionView? _collectionView;
    private GridView? _gridView;
    private ScrollViewer? _scrollViewer;
    private bool _loadQueued;
    private int _generation;

    public static readonly BindableProperty LoadMoreCommandProperty = BindableProperty.Create(
        nameof(LoadMoreCommand), typeof(ICommand), typeof(StickerInfiniteScrollBehavior));
    public static readonly BindableProperty HasMoreItemsProperty = BindableProperty.Create(
        nameof(HasMoreItems), typeof(bool), typeof(StickerInfiniteScrollBehavior), false);
    public static readonly BindableProperty IsLoadingProperty = BindableProperty.Create(
        nameof(IsLoading), typeof(bool), typeof(StickerInfiniteScrollBehavior), false,
        propertyChanged: OnLoadingChanged);

    public ICommand? LoadMoreCommand
    {
        get => (ICommand?)GetValue(LoadMoreCommandProperty);
        set => SetValue(LoadMoreCommandProperty, value);
    }

    public bool HasMoreItems
    {
        get => (bool)GetValue(HasMoreItemsProperty);
        set => SetValue(HasMoreItemsProperty, value);
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    protected override void OnAttachedTo(CollectionView bindable, GridView platformView)
    {
        base.OnAttachedTo(bindable, platformView);
        _collectionView = bindable;
        _gridView = platformView;
        bindable.BindingContextChanged += OnBindingContextChanged;
        BindingContext = bindable.BindingContext;
        platformView.Loaded += OnLoaded;
        platformView.Unloaded += OnUnloaded;
        platformView.LayoutUpdated += OnLayoutUpdated;
        AttachScrollViewer();
    }

    protected override void OnDetachedFrom(CollectionView bindable, GridView platformView)
    {
        platformView.Loaded -= OnLoaded;
        platformView.Unloaded -= OnUnloaded;
        platformView.LayoutUpdated -= OnLayoutUpdated;
        bindable.BindingContextChanged -= OnBindingContextChanged;
        DetachScrollViewer();
        ResetPaging();
        _gridView = null;
        _collectionView = null;
        BindingContext = null;
        base.OnDetachedFrom(bindable, platformView);
    }

    private static void OnLoadingChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (newValue is true) ((StickerInfiniteScrollBehavior)bindable).ResetPaging();
    }

    private void OnBindingContextChanged(object? sender, EventArgs eventArgs)
    {
        ResetPaging();
        BindingContext = _collectionView?.BindingContext;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs) => AttachScrollViewer();

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        DetachScrollViewer();
        ResetPaging();
    }

    // LayoutUpdated has a null sender. Use it only to discover a template that
    // was not yet available at Loaded; never use it to hold a paging lock.
    private void OnLayoutUpdated(object? sender, object eventArgs)
    {
        if (_scrollViewer is null && _gridView?.IsLoaded == true) AttachScrollViewer();
    }

    private void AttachScrollViewer()
    {
        var viewer = _gridView is null ? null : FindScrollViewer(_gridView);
        if (ReferenceEquals(_scrollViewer, viewer)) return;
        DetachScrollViewer();
        _scrollViewer = viewer;
        if (viewer is not null) viewer.ViewChanged += OnViewChanged;
    }

    private void DetachScrollViewer()
    {
        if (_scrollViewer is not null) _scrollViewer.ViewChanged -= OnViewChanged;
        _scrollViewer = null;
    }

    private void ResetPaging()
    {
        _generation++;
        _loadQueued = false;
        _paging.Reset();
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs eventArgs)
    {
        var viewer = _scrollViewer;
        var grid = _gridView;
        if (viewer is null || grid is null || _collectionView?.IsVisible != true) return;
        if (!_paging.TryRequest(viewer.VerticalOffset, viewer.ScrollableHeight,
                HasMoreItems, IsLoading || _loadQueued)) return;
        var command = LoadMoreCommand;
        if (command is null || !command.CanExecute(null))
        {
            _paging.Reset();
            return;
        }

        var generation = _generation;
        var count = grid.Items.Count;
        var firstItem = count > 0 ? grid.Items[0] : null;
        _loadQueued = true;
        if (!grid.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (generation != _generation) return;
            try
            {
                if (!ReferenceEquals(grid, _gridView) || !grid.IsLoaded ||
                    _collectionView?.IsVisible != true || !HasMoreItems || IsLoading ||
                    grid.Items.Count != count || count == 0 ||
                    !ReferenceEquals(firstItem, grid.Items[0]) || !command.CanExecute(null)) return;

                // Mutate only after the native scroll/layout callback has returned.
                // KeepScrollOffset handles preservation without ChangeView/ScrollIntoView.
                command.Execute(null);
            }
            finally
            {
                if (generation == _generation) _loadQueued = false;
            }
        }))
        {
            _loadQueued = false;
            _paging.Reset();
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject view)
    {
        if (view is ScrollViewer viewer) return viewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(view); index++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(view, index));
            if (found is not null) return found;
        }
        return null;
    }
}
