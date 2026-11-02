using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.App.Views.Controls;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The mapping surface's two keyboard-facing contracts:
///
/// 1. The model stage reserves a header-safe region. Opening the editor lifts the model with
///    a render transform, and a transform moves pixels without re-laying anything out, so
///    nothing but reserved space stops the shoulder buttons from riding up over the stage's
///    title and instructions. The stage must keep that space.
/// 2. A mapping row is an activation target, not a label: it takes keyboard focus, rings in
///    the accent, and Enter/Space runs exactly the action a click runs - select the row and
///    open its editor - with the pointer's wash held while the key is down. A key that lands
///    on a button inside the row stays with that button.
/// </summary>
public sealed class MappingRowKeyboardTests
{
    private const string DeviceId = "pad-01";

    private static (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider) BuildWindow()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host);
        window.Show();
        return (window, viewModel, host, provider);
    }

    private static void ConnectPad(FakeGamepadProvider provider)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
    }

    private static void Click(Control target, Window window)
    {
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static Control ModelElement(Window window, string name)
        => window.FindControl<GamepadVisualizer>("GamepadVisualizer")!.FindControl<Control>(name)!;

    private static Viewbox ModelViewbox(Window window)
        => window.FindControl<GamepadVisualizer>("GamepadVisualizer")!
            .GetVisualAncestors().OfType<Viewbox>().First();

    private static MappingRowBorder RowFor(Window window, MappingRowViewModel row)
    {
        var list = window.FindControl<ItemsControl>("MappingsList")!;
        return list.GetVisualDescendants().OfType<MappingRowBorder>().Single(candidate => candidate.DataContext == row);
    }

    private static Rect RectInWindow(Control control, Window window)
    {
        var origin = control.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(origin);
        return new Rect(origin!.Value, control.Bounds.Size);
    }

    /// <summary>
    /// The stage's header: every text the stage carries outside the model itself - the title
    /// and the instructions. Found structurally rather than by copy so the region is defined
    /// by where the text lives, not by what it says.
    /// </summary>
    private static List<TextBlock> HeaderTexts(Window window)
    {
        var viewbox = ModelViewbox(window);
        var stage = (Panel)viewbox.Parent!;
        var modelTexts = viewbox.GetVisualDescendants().OfType<TextBlock>().ToHashSet();

        var header = stage.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => !modelTexts.Contains(text))
            .ToList();

        Assert.Equal(2, header.Count);
        return header;
    }

    private static Rect HeaderRect(Window window)
    {
        var rects = HeaderTexts(window).Select(text => RectInWindow(text, window)).ToList();
        var top = rects.Min(rect => rect.Top);
        var bottom = rects.Max(rect => rect.Bottom);
        var left = rects.Min(rect => rect.Left);
        var right = rects.Max(rect => rect.Right);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static Rect ModelRect(Window window)
    {
        var viewbox = ModelViewbox(window);
        return RectInWindow(viewbox, window);
    }

    private static Color ColorOf(IBrush? brush)
        => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

    /// <summary>Colour equality with a failure message: xUnit v3's Assert.Equal takes none for a Color.</summary>
    private static void AssertColor(Color expected, Color actual, string message)
        => Assert.True(expected == actual, $"{message}: expected #{expected}, got #{actual}");

    private static Color Token(ThemeVariant variant, string key)
    {
        var app = Application.Current!;
        app.TryFindResource(key, variant, out var brush);
        return ColorOf(brush as IBrush);
    }

    // ---- The model stays inside its reserved region while editing -------------

    [AvaloniaFact]
    public void Editing_Lifts_The_Model_Without_Overlapping_The_Stage_Header()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var lift = Assert.IsType<TranslateTransform>(ModelViewbox(window).RenderTransform);

        // Headless runs no clock, so the animated lift never leaves its start value here.
        // Detaching the transition makes the animation's TARGET observable, exactly as
        // Opening_The_Editor_Lifts_The_Model_With_An_Animated_Transform does.
        lift.Transitions = null;

        var header = HeaderRect(window);
        var restTop = ModelRect(window).Top;

        Click(ModelElement(window, "ButtonA"), window);
        Assert.True(viewModel.IsEditing);

        // The fix reserves room for the lift; it must not remove the motion itself.
        Assert.True(lift.Y < 0d, $"the model must still lift while the editor is open, Y was {lift.Y}");

        var liftedTop = ModelRect(window).Top;
        Assert.True(liftedTop < restTop, "the lift must actually carry the model upward");

        Assert.True(liftedTop >= header.Bottom,
            $"the lifted model (top {liftedTop}) overlaps the stage header (bottom {header.Bottom})");
    }

    // ---- A row is a keyboard activation target --------------------------------

    [AvaloniaFact]
    public void A_Mapping_Row_Takes_Keyboard_Focus_And_Rings_In_The_Accent()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var row = RowFor(window, viewModel.Mappings.First(
            candidate => candidate.Entry.Source == new ButtonBinding(GamepadButton.B)));

        Assert.True(row.Focusable, "a mapping row must be focusable so the keyboard can reach it");
        AssertColor(Token(row.ActualThemeVariant, "CardBorderBrush"), ColorOf(row.BorderBrush),
            "an unfocused row wears the card border");

        Assert.True(row.Focus(), "a mapping row must take keyboard focus");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(row.IsFocused);
        AssertColor(Token(row.ActualThemeVariant, "AccentBrush"), ColorOf(row.BorderBrush),
            "a focused row must ring in the accent");
    }

    [AvaloniaFact]
    public void Enter_On_A_Focused_Row_Opens_Its_Editor_With_Pressed_Feedback()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var row = RowFor(window, viewModel.Mappings.First(
            candidate => candidate.Entry.Source == new ButtonBinding(GamepadButton.B)));

        HoldActivationKey(window, row, Key.Enter, PhysicalKey.Enter);

        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        AssertColor(Token(row.ActualThemeVariant, "CardBrush"), ColorOf(row.Background),
            "releasing must drop the pressed wash");

        // The same editor a click opens: the row is selected and its entry is loaded.
        Assert.True(viewModel.IsEditing, "Enter on a focused row must open that row's editor");
        Assert.Same(row.DataContext, viewModel.SelectedMapping);
        Assert.Equal(GamepadButton.B, viewModel.Editor.SelectedButton);
        Assert.Equal(MappingTargetKind.KeyPress, viewModel.Editor.TargetKind);
        Assert.Equal(GamepadKey.Enter, viewModel.Editor.SelectedKey);
    }

    [AvaloniaFact]
    public void Space_On_A_Focused_Row_Opens_Its_Editor_With_Pressed_Feedback()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var row = RowFor(window, viewModel.Mappings.First(
            candidate => candidate.Entry.Source == new ButtonBinding(GamepadButton.B)));

        HoldActivationKey(window, row, Key.Space, PhysicalKey.Space);

        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        AssertColor(Token(row.ActualThemeVariant, "CardBrush"), ColorOf(row.Background),
            "releasing must drop the pressed wash");
        Assert.True(viewModel.IsEditing, "Space on a focused row must open that row's editor");
        Assert.Same(row.DataContext, viewModel.SelectedMapping);
    }

    /// <summary>
    /// Focuses the row and holds the activation key down, asserting the pressed feedback is
    /// visible while it is held and that the editor has not opened yet - the action fires on
    /// release, the way a button's does.
    /// </summary>
    private static void HoldActivationKey(Window window, MappingRowBorder row, Key key, PhysicalKey physicalKey)
    {
        // The row's background fades (an inline BrushTransition), and headless runs no clock,
        // so the transition is detached to read the pressed TARGET, as the hover tests do.
        row.Transitions = null;

        window.Focus();
        Assert.True(row.Focus());
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        AssertColor(Token(row.ActualThemeVariant, "ControlHoverBrush"), ColorOf(row.Background),
            "a held row must show the pressed wash, consistent with the pointer");
        Assert.False(window.DataContext is MainWindowViewModel vm && vm.IsEditing,
            "the editor opens on release, not while the key is held");
    }

    [AvaloniaFact]
    public void A_Key_That_Lands_On_A_Row_Button_Stays_With_That_Button()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var rowViewModel = viewModel.Mappings.First(
            candidate => candidate.Entry.Source == new ButtonBinding(GamepadButton.B));
        var row = RowFor(window, rowViewModel);
        var toggle = row.GetVisualDescendants().OfType<ToggleButton>().First();

        window.Focus();
        Assert.True(toggle.Focus());
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var enabledBefore = rowViewModel.IsEnabled;

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Space belongs to the toggle it landed on: the row must not also open its editor,
        // exactly as a click on the toggle never opens it either.
        Assert.False(viewModel.IsEditing, "a key press on the row's toggle must not open the editor");
        Assert.NotEqual(enabledBefore, rowViewModel.IsEnabled);
    }

    [AvaloniaFact]
    public void A_Key_That_Lands_On_The_Rows_Delete_Button_Stays_With_That_Button()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var rowViewModel = viewModel.Mappings.First(
            candidate => candidate.Entry.Source == new ButtonBinding(GamepadButton.B));
        var row = RowFor(window, rowViewModel);
        var delete = row.GetVisualDescendants().OfType<Button>()
            .First(button => button.Content as string == "删除");

        window.Focus();
        Assert.True(delete.Focus());
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Space belongs to the delete button it landed on: the row must not also open its
        // editor, and the mapping the button owns is the one that goes away.
        Assert.False(viewModel.IsEditing, "a key press on the row's delete button must not open the editor");
        Assert.DoesNotContain(viewModel.Mappings,
            candidate => candidate.Entry.Source == new ButtonBinding(GamepadButton.B));
    }
}
