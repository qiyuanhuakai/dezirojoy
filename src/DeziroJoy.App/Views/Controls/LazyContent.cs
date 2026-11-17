using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// A content host that builds its content the first time it is asked for and releases it
/// again the moment it is not. The content is declared as a <see cref="ContentFactory"/> -
/// a template the XAML compiler keeps unbuilt until the host fills it - so a heavy surface
/// (the settings overlay, the mapping editor, the sensitivity panels) costs nothing to
/// construct until the user opens it for the first time, instead of being built once at
/// window startup and merely hidden.
/// <para>
/// Closing releases the content for good: the panel is detached and its DataContext
/// cleared, and with them every binding and code-behind subscription it made. Opening and
/// closing a surface any number of times therefore leaves exactly one live panel behind -
/// the open one - rather than one per visit. The next open builds a fresh panel.
/// </para>
/// <para>
/// The factory deliberately does not ride <see cref="ContentControl.ContentTemplate"/>: a
/// ContentPresenter that has a template sets its own DataContext to the content, which would
/// leave the built panel inheriting itself instead of the window's view model.
/// </para>
/// </summary>
public sealed class LazyContent : ContentControl
{
    /// <summary>Defines the <see cref="IsActive"/> property.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LazyContent, bool>(nameof(IsActive));

    /// <summary>Defines the <see cref="ContentFactory"/> property.</summary>
    public static readonly StyledProperty<IDataTemplate?> ContentFactoryProperty =
        AvaloniaProperty.Register<LazyContent, IDataTemplate?>(nameof(ContentFactory));

    /// <summary>
    /// Whether the content should exist. False at rest: nothing is built until the first
    /// true, and whatever was built is released again on the way back to false.
    /// </summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The template the host fills when it becomes active.</summary>
    public IDataTemplate? ContentFactory
    {
        get => GetValue(ContentFactoryProperty);
        set => SetValue(ContentFactoryProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsActiveProperty)
        {
            UpdateContent();
        }
    }

    private void UpdateContent()
    {
        if (IsActive)
        {
            if (Content is null)
            {
                Content = ContentFactory?.Build(DataContext);
            }

            return;
        }

        if (Content is not Control content)
        {
            return;
        }

        // Detach first: a panel that subscribes on Loaded answers Unloaded by reading its
        // DataContext, so the DataContext has to survive the detach for that answer to
        // find what it subscribed to.
        Content = null;

        // A detached control usually loses its inherited DataContext, but a panel whose
        // code-behind subscribes to its view model is only released once the DataContext is
        // gone - clearing it here makes that release deterministic rather than inherited.
        content.DataContext = null;
    }
}
