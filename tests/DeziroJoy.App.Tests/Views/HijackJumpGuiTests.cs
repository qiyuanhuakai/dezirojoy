using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The 手柄劫持（临时） jump through the real window: a press opens the element's mapping
/// configuration and the window scrolls that element's card into view in the right panel.
/// The switch's tooltip and the panel's hint describe the behaviour, a stick deflection
/// opens nothing, and turning the hijack off hands the press back to the mapping.
/// </summary>
public sealed class HijackJumpGuiTests
{
    private const string DeviceId = "pad-01";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 64.0);

    /// <summary>
    /// A window over a profile long enough that the mappings list must scroll: 24 distinct
    /// extra-button cards, the last one far below the fold.
    /// </summary>
    private static (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider) BuildWindow()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var viewModel = new MainWindowViewModel(host, store);

        var presets = new PresetStore(store);
        presets.SaveSlot(DeviceId, 1, new GamepadProfile
        {
            Name = "跳转测试配置",
            DeviceId = DeviceId,
            Layout = GamepadLayout.Xbox,
            Mappings = Enumerable.Range(1, 24)
                .Select(code => new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.Extra, code),
                    Target = new KeyPressTarget(GamepadKey.F13),
                })
                .ToList(),
        });

        viewModel.SetGamepadNavigationEnabled(true);
        var window = new MainWindow(viewModel, host);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel, host, provider);
    }

    private static void ConnectPad(FakeGamepadProvider provider, IReadOnlyList<int>? extraButtons = null)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f, extraButtons));
    }

    private static void PadStep(EngineHost host, FakeGamepadProvider provider, Window window, IGamepadState state)
    {
        provider.SetState(DeviceId, state);
        host.TickOnce(Tick);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static ItemsControl MappingsList(Window window) => window.FindControl<ItemsControl>("MappingsList")!;

    private static ScrollViewer MappingsScroller(Window window)
        => MappingsList(window).GetVisualAncestors().OfType<ScrollViewer>().First();

    /// <summary>The container the list realized for one card, or null when it has none yet.</summary>
    private static Control? CardContainer(Window window, MappingCardViewModel card)
        => MappingsList(window).GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control => ReferenceEquals(control.DataContext, card));

    [AvaloniaFact]
    public void A_Press_Opens_The_Editor_And_Scrolls_The_Card_Into_View()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        PadStep(host, provider, window, GamepadState.Create(default, 0f, 0f, 0f, 0f));

        var lastCard = viewModel.MappingCards[^1];
        var lastSource = Assert.IsType<ButtonBinding>(lastCard.Source);
        Assert.Equal(24, lastSource.Code);

        var scroller = MappingsScroller(window);
        Assert.True(scroller.Offset.Y < 1, "the list starts at the top, so the last card is below the fold");

        // Press the last extra button: the jump opens its configuration and the window
        // scrolls its card into view.
        PadStep(host, provider, window, GamepadState.Create(default, 0f, 0f, 0f, 0f, [lastSource.Code]));

        Assert.True(viewModel.IsEditing);
        Assert.Equal(GamepadButton.Extra, viewModel.Editor.SelectedButton);
        Assert.Equal(24, viewModel.Editor.SelectedExtraCode);
        Assert.True(window.FindControl<Border>("EditorPanel")!.IsVisible);

        var container = CardContainer(window, lastCard);
        Assert.NotNull(container);
        var cardOrigin = container!.TranslatePoint(new Point(0, 0), window);
        var scrollerOrigin = scroller.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(cardOrigin);
        Assert.NotNull(scrollerOrigin);

        var cardTop = cardOrigin!.Value.Y;
        var scrollerBottom = scrollerOrigin!.Value.Y + scroller.Bounds.Height;
        Assert.True(cardTop < scrollerBottom, "the jumped-to card is inside the scroller's viewport");
        Assert.True(scroller.Offset.Y > 0, "the list scrolled to reach it");
    }

    [AvaloniaFact]
    public void An_Unmapped_Button_Opens_A_Draft_Through_The_Window()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        PadStep(host, provider, window, GamepadState.Create(default, 0f, 0f, 0f, 0f));

        // LeftBumper carries nothing in this profile: a fresh draft opens for it.
        PadStep(host, provider, window, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        PadStep(host, provider, window, GamepadState.Create(GamepadButtonMask.LeftBumper, 0f, 0f, 0f, 0f));

        Assert.True(viewModel.IsEditing);
        Assert.Equal(GamepadButton.LeftBumper, viewModel.Editor.SelectedButton);
        Assert.True(window.FindControl<Border>("EditorPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void A_Stick_Deflection_Opens_Nothing_Through_The_Window()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        PadStep(host, provider, window, GamepadState.Create(default, 0f, 0f, 0f, 0f));

        PadStep(host, provider, window, GamepadState.Create(default, 0f, 0f, 0.9f, 0f));

        Assert.Equal(new StickBinding(GamepadStick.Right), viewModel.SelectedSource);
        Assert.False(viewModel.IsEditing);
        Assert.False(window.FindControl<Border>("EditorPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void The_ToolTip_And_The_Right_Panel_Hint_Describe_The_Jump()
    {
        var (window, _, _, _) = BuildWindow();

        var toggle = window.FindControl<ToggleButton>("GamepadHijackToggle")!;
        var tooltip = toggle.GetValue(ToolTip.TipProperty);
        var tooltipText = Assert.IsType<string>(tooltip);
        Assert.Contains("跳转", tooltipText);

        var hint = window.FindControl<TextBlock>("MappingsListHint")!;
        Assert.Contains("手柄劫持", hint.Text);
        Assert.Contains("跳转", hint.Text);
    }
}
