using Avalonia;
using Avalonia.Controls;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// The right column's mapping-card list. Its host declares a <see cref="VirtualizingStackPanel"/>
/// for it, so a profile with dozens of cards realizes only the ones on screen instead of the
/// whole list.
/// <para>
/// It also answers <see cref="MainWindowViewModel.MappingCardVisibleRequested"/> itself. The
/// window's own handler for that request looks for the card's realized container, which a
/// virtualized list does not build for a card below the fold - the hijack jump's
/// scroll-into-view would silently stop working the day the list virtualized. Answering with
/// <see cref="ItemsControl.ScrollIntoView"/> realizes the card and scrolls to it, and the
/// window's handler still runs unchanged for the cards that are already realized.
/// </para>
/// <para>
/// The subscription is adopted when the list enters a tree over the view model and released
/// when it leaves one: a window closed must not keep answering requests, or a window rebuilt
/// over the same view model would stack a second subscription.
/// </para>
/// </summary>
public sealed class LazyMappingList : ItemsControl
{
    private MainWindowViewModel? _viewModel;

    /// <summary>
    /// A derived control does not inherit the base control's implicit theme - the theme is
    /// looked up as a resource under the control's own style key - so without this the list
    /// would carry no ItemsControl template at all and render nothing.
    /// </summary>
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        SyncSubscription();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncSubscription();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_viewModel is { } viewModel)
        {
            _viewModel = null;
            viewModel.MappingCardVisibleRequested -= OnMappingCardVisibleRequested;
        }
    }

    private void SyncSubscription()
    {
        var next = DataContext as MainWindowViewModel;

        if (ReferenceEquals(_viewModel, next))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.MappingCardVisibleRequested -= OnMappingCardVisibleRequested;
        }

        _viewModel = next;

        if (_viewModel is not null)
        {
            _viewModel.MappingCardVisibleRequested += OnMappingCardVisibleRequested;
        }
    }

    private void OnMappingCardVisibleRequested(MappingCardViewModel card) => ScrollIntoView(card);
}
