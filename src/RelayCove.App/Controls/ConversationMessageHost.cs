using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public sealed class ConversationMessageHost : ContentView
{
    private readonly Grid _presentationsRoot = new();
    private readonly Dictionary<ConversationMessagePresentation, MessageListView> _views = [];

    public ConversationMessageHost()
    {
        IsClippedToBounds = true;
        Content = _presentationsRoot;
    }

    public static readonly BindableProperty PresentationsProperty = BindableProperty.Create(
        nameof(Presentations),
        typeof(ObservableCollection<ConversationMessagePresentation>),
        typeof(ConversationMessageHost),
        propertyChanged: OnPresentationsChanged);

    public ObservableCollection<ConversationMessagePresentation>? Presentations
    {
        get => (ObservableCollection<ConversationMessagePresentation>?)GetValue(PresentationsProperty);
        set => SetValue(PresentationsProperty, value);
    }

    private static void OnPresentationsChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var host = (ConversationMessageHost)bindable;
        if (oldValue is ObservableCollection<ConversationMessagePresentation> previous)
            previous.CollectionChanged -= host.OnPresentationsCollectionChanged;
        host.ClearPresentations();
        if (newValue is not ObservableCollection<ConversationMessagePresentation> current) return;
        current.CollectionChanged += host.OnPresentationsCollectionChanged;
        foreach (var presentation in current) host.AddPresentation(presentation);
        host.ApplyActivePresentation();
    }

    private void OnPresentationsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.Action == NotifyCollectionChangedAction.Reset)
        {
            ClearPresentations();
            if (Presentations is not null)
            {
                foreach (var presentation in Presentations) AddPresentation(presentation);
            }
            ApplyActivePresentation();
            return;
        }

        if (eventArgs.OldItems is not null)
        {
            foreach (ConversationMessagePresentation presentation in eventArgs.OldItems)
                RemovePresentation(presentation);
        }
        if (eventArgs.NewItems is not null)
        {
            foreach (ConversationMessagePresentation presentation in eventArgs.NewItems)
                AddPresentation(presentation);
        }
        ApplyActivePresentation();
    }

    private void AddPresentation(ConversationMessagePresentation presentation)
    {
        if (_views.ContainsKey(presentation)) return;
        var view = new MessageListView
        {
            BindingContext = presentation.ViewModel,
            ConversationKey = presentation.ConversationKey,
            MessageItems = presentation.Messages,
            IsVisible = false
        };
        presentation.PropertyChanged += OnPresentationPropertyChanged;
        _views.Add(presentation, view);
        _presentationsRoot.Children.Add(view);
    }

    private void RemovePresentation(ConversationMessagePresentation presentation)
    {
        presentation.PropertyChanged -= OnPresentationPropertyChanged;
        if (!_views.Remove(presentation, out var view)) return;
        _presentationsRoot.Children.Remove(view);
        view.MessageItems = null;
        view.BindingContext = null;
    }

    private void ClearPresentations()
    {
        foreach (var presentation in _views.Keys.ToArray()) RemovePresentation(presentation);
    }

    private void OnPresentationPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ConversationMessagePresentation.IsActive))
            ApplyActivePresentation();
    }

    private void ApplyActivePresentation()
    {
        foreach (var (presentation, view) in _views)
        {
            var isActive = presentation.IsActive;
            view.IsVisible = isActive;
            view.InputTransparent = !isActive;
        }
    }
}
