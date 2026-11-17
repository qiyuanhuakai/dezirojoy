using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.App.Views.Controls;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The right column's mapping list is virtualized: only the cards on screen are realized,
/// so a long profile costs a handful of containers instead of the whole list. These tests
/// pin the parts that virtualization could have broken - scrolling, editing a row, the
/// selected row's highlight, keyboard focus, and the connector line the model draws to the
/// editor card while a mapping is open.
/// </summary>
public sealed class MappingListVirtualizationTests
{
    private const string DeviceId = "pad-01";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 64.0);

    /// <summary>
    /// A window over a profile long enough that the mappings list must scroll: 24 distinct
    /// extra-button cards, the last one far below the fold.
    /// </summary>
    private static (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider) BuildWindow(
        bool longProfile = true)
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var viewModel = new MainWindowViewModel(host, store);

        if (!longProfile)
        {
            var plain = new MainWindow(viewModel, host);
            plain.Show();
            Dispatcher.UIThread.RunJobs();
            return (plain, viewModel, host, provider);
        }

        var presets = new PresetStore(store);
        presets.SaveSlot(DeviceId, 1, new GamepadProfile
        {
            Name = "虚拟化测试配置",
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

        var window = new MainWindow(viewModel, host);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel, host, provider);
    }

    private static void ConnectPad(FakeGamepadProvider provider)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
    }

    private static void PadStep(EngineHost host, FakeGamepadProvider provider, Window window, IGamepadState state)
    {
        provider.SetState(DeviceId, state);
        host.TickOnce(Tick);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static ItemsControl List(Window window) => window.FindControl<ItemsControl>("MappingsList")!;

    private static ScrollViewer Scroller(Window window)
        => List(window).GetVisualAncestors().OfType<ScrollViewer>().First();

    private static List<Control> RealizedCards(Window window)
        => List(window).GetVisualDescendants().OfType<Control>()
            .Where(control => control.Classes.Contains("mappingCard"))
            .ToList();

    private static List<MappingRowBorder> RealizedRows(Window window)
        => List(window).GetVisualDescendants().OfType<MappingRowBorder>().ToList();

    private static Color Token(ThemeVariant variant, string key)
    {
        var app = Application.Current!;
        app.TryFindResource(key, variant, out var brush);
        return Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;
    }

    private static void Click(Control target, Window window)
    {
        Settle(window);
        target.BringIntoView();
        Settle(window);
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        Settle(window);
    }

    // ---- The list virtualizes ------------------------------------------------

    [AvaloniaFact]
    public void The_Mapping_List_Realizes_Only_The_Cards_On_Screen()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        PadStep(host, provider, window, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        Settle(window);

        Assert.Equal(24, viewModel.MappingCards.Count);

        var cards = RealizedCards(window);
        Assert.NotEmpty(cards);
        Assert.True(
            cards.Count < 12,
            $"a 24-card profile must not realize every card: realized {cards.Count}");

        var scroller = Scroller(window);
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height,
            $"the list must scroll: extent {scroller.Extent.Height}, viewport {scroller.Viewport.Height}");
        Assert.True(scroller.Offset.Y < 1, "the list starts at the top");
    }

    [AvaloniaFact]
    public void Scrolling_Realizes_The_Cards_The_Viewport_Moves_To()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        PadStep(host, provider, window, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        Settle(window);

        var scroller = Scroller(window);
        var top = RealizedCards(window).First();

        scroller.Offset = new Vector(0, scroller.Extent.Height);
        Settle(window);

        Assert.True(scroller.Offset.Y > 0, $"the list must scroll, offset is {scroller.Offset.Y}");
        var bottom = RealizedCards(window).Last();
        Assert.True(!ReferenceEquals(top, bottom), "scrolling must realize different cards, not keep the first ones");

        scroller.Offset = new Vector(0, 0);
        Settle(window);
        Assert.True(scroller.Offset.Y < 1, "the list must scroll back to the top");
        Assert.Same(viewModel.MappingCards[0], RealizedCards(window).First().DataContext);
    }

    // ---- Selection survives virtualization -----------------------------------

    [AvaloniaFact]
    public void The_Selected_Row_Keeps_Its_Tint_After_The_List_Scrolls_Away_And_Back()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        PadStep(host, provider, window, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        Settle(window);

        var card = viewModel.MappingCards[0];
        var row = card.Mappings[0];
        viewModel.SelectedMapping = row;
        Settle(window);

        var selected = RealizedRows(window).Single(candidate => candidate.DataContext == row);
        Assert.True(TintOf(selected) is not null, "the selected row carries its tint while it is on screen");

        // Scroll the row out of view and back: the container is recycled on the way, so the
        // tint has to be re-established by the row's own bindings rather than remembered.
        var scroller = Scroller(window);
        scroller.Offset = new Vector(0, scroller.Extent.Height);
        Settle(window);
        Assert.DoesNotContain(RealizedRows(window), candidate => candidate.DataContext == row);

        scroller.Offset = new Vector(0, 0);
        Settle(window);

        var returned = RealizedRows(window).Single(candidate => candidate.DataContext == row);
        Assert.True(TintOf(returned) is not null, "the selected row must wear its tint again after scrolling back");
    }

    private static Border? TintOf(MappingRowBorder row)
        => row.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.IsVisible && border.Background is ISolidColorBrush solid
                && solid.Color == Token(row.ActualThemeVariant, "RowSelectedBrush"));

    // ---- Editing through the virtualized list --------------------------------

    [AvaloniaFact]
    public void A_Row_Scrolled_Into_View_Still_Opens_Its_Editor_And_Commits()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        PadStep(host, provider, window, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        Settle(window);

        // Scroll to the far end of the list and edit the last visible row from there.
        var scroller = Scroller(window);
        scroller.Offset = new Vector(0, scroller.Extent.Height);
        Settle(window);

        var row = RealizedRows(window).Last();
        var rowViewModel = Assert.IsType<MappingRowViewModel>(row.DataContext);
        var source = rowViewModel.Entry.Source;

        Click(row, window);

        Assert.True(viewModel.IsEditing, "a row below the fold must still open its editor");
        Assert.Same(rowViewModel, viewModel.SelectedMapping);
        Assert.NotNull(window.GetLogicalDescendants().OfType<MappingEditorPanel>().FirstOrDefault());

        var editor = window.GetLogicalDescendants().OfType<MappingEditorPanel>().First();
        editor.FindControl<ComboBox>("TargetKindCombo")!.SelectedItem = MappingTargetKind.KeyPress;
        viewModel.Editor.KeyPickerExpanded = true;
        Settle(window);

        // The loaded draft opens on the category its own key lives in, so the system keys
        // are opened before Space can be picked - what a user does.
        var navigator = editor.FindControl<ItemsControl>("KeyCategoryNav")!;
        var systemKeys = navigator.GetVisualDescendants().OfType<RadioButton>()
            .First(button => (string?)button.Content == "系统键");
        Click(systemKeys, window);

        var key = editor.GetLogicalDescendants().OfType<Button>()
            .First(button => button.DataContext is KeyPickerItem { Key: GamepadKey.Space });
        Click(key, window);

        Assert.Equal(GamepadKey.Space, viewModel.Editor.SelectedKey);

        Click(editor.FindControl<Button>("CommitButton")!, window);

        Assert.False(viewModel.IsEditing);
        Assert.True(window.GetLogicalDescendants().OfType<MappingEditorPanel>().FirstOrDefault() is null,
            "committing releases the editor");
        Assert.Contains(
            viewModel.Mappings,
            candidate => candidate.Entry.Source == source
                && candidate.Entry.Target is KeyPressTarget { Key: GamepadKey.Space });
    }

    // ---- Focus through the virtualized list ----------------------------------

    [AvaloniaFact]
    public void A_Virtualized_Row_Takes_Keyboard_Focus_And_Rings_In_The_Accent()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        PadStep(host, provider, window, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        Settle(window);

        var scroller = Scroller(window);
        scroller.Offset = new Vector(0, scroller.Extent.Height);
        Settle(window);

        var row = RealizedRows(window).Last();

        Assert.True(row.Focusable, "a mapping row must stay focusable under virtualization");
        Assert.True(row.Focus());
        Settle(window);

        Assert.True(row.IsFocused);
        Assert.Equal(Token(row.ActualThemeVariant, "AccentBrush"), ColorOf(row.BorderBrush));
    }

    private static Color ColorOf(IBrush? brush)
        => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

    // ---- The connector line stays where the editor card is -------------------

    [AvaloniaFact]
    public void The_Connector_Line_Is_Drawn_While_The_List_Is_Scrolled()
    {
        // The default profile: its mappings sit on the model's own elements, which is where
        // the connector line starts.
        var (window, viewModel, host, provider) = BuildWindow(longProfile: false);
        ConnectPad(provider);
        PadStep(host, provider, window, GamepadState.Create(default, 0f, 0f, 0f, 0f));
        Settle(window);

        // B's card is the seventh of ten, so the list is scrolled to it before the row is
        // clicked - the editor opens from a card below the fold.
        var scroller = Scroller(window);
        var card = viewModel.MappingCards.First(
            candidate => candidate.Source is ButtonBinding { Button: GamepadButton.B });
        List(window).ScrollIntoView(card);
        Settle(window);

        var row = RealizedRows(window).Single(
            candidate => candidate.DataContext is MappingRowViewModel { Entry.Source: ButtonBinding { Button: GamepadButton.B } });
        Click(row, window);
        Settle(window);

        var connector = window.FindControl<Canvas>("ConnectorLayer")!;
        var line = connector.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .FirstOrDefault(path => path.Data is not null);

        Assert.NotNull(line);
        Assert.True(line!.Bounds.Width > 0 && line.Bounds.Height > 0,
            $"the connector must be drawn from the model to the editor card, it is {line.Bounds}");

        // The line ends on the editor card, which is a fixed frame in the centre column:
        // its end point must sit inside that card's own bounds.
        var editor = window.FindControl<Border>("EditorPanel")!;
        var origin = line.TranslatePoint(new Point(0, 0), editor);
        Assert.NotNull(origin);
        Assert.True(
            new Rect(default, editor.Bounds.Size).Contains(origin!.Value + line.Bounds.Position)
            || new Rect(default, editor.Bounds.Size).Intersects(line.Bounds),
            $"the connector must end on the editor card: line {line.Bounds} in card {editor.Bounds}");
    }
}
