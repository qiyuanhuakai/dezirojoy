using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
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
/// The 全局特殊键 area at the top of the 按键配置 page, and the collision hints it, the
/// mapping rows and the mapping editor show. The area leads the page because the two keys
/// are device-level configuration; it is pinned above the mapping list rather than scrolling
/// with it, so the keys and their warnings stay in view while the rows scroll under them.
/// </summary>
public sealed class SpecialKeysPanelGuiTests
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
        provider.SetState(DeviceId, GamepadState.Neutral);
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

    private static ItemsControl SpecialKeysList(Window window)
        => window.FindControl<ItemsControl>("SpecialKeysList")!;

    private static ItemsControl MappingsList(Window window)
        => window.FindControl<ItemsControl>("MappingsList")!;

    [AvaloniaFact]
    public void The_Global_Special_Keys_Section_Sits_Above_The_Mapping_List()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var section = SpecialKeysList(window);
        var list = MappingsList(window);

        Assert.True(section.IsVisible);
        Assert.True(list.IsVisible);

        var sectionBottom = section.TranslatePoint(new Point(0, section.Bounds.Height), window)!.Value.Y;
        var listTop = list.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.True(sectionBottom <= listTop,
            $"the special keys section ends at {sectionBottom} but the mapping list starts at {listTop}");

        // The section is pinned: it does not live inside the rows' scroller, so it stays put
        // while the rows scroll under it.
        var scroller = list.GetVisualAncestors().OfType<ScrollViewer>().First();
        Assert.DoesNotContain(section, scroller.GetLogicalDescendants());
    }

    [AvaloniaFact]
    public void The_Section_Is_Titled_全局特殊键_In_Chinese()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var panel = window.FindControl<Border>("RightPanel")!;
        var texts = panel.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();

        Assert.Contains("全局特殊键", texts);
    }

    [AvaloniaFact]
    public void An_Unconfigured_Kind_Shows_The_Guidance_And_No_Edit_Affordance()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var items = SpecialKeysList(window)
            .GetVisualDescendants()
            .OfType<StackPanel>()
            .Where(panel => panel.DataContext is GlobalSpecialKeyItem)
            .ToList();

        Assert.Equal(2, items.Count);

        foreach (var item in items)
        {
            var texts = item.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
            Assert.Contains("未配置", texts);
            Assert.Contains(texts, text => text!.Contains("特殊键"));

            // The 编辑 affordance exists only for a configured kind: an unconfigured row has
            // no mapping to open, so its button stays out of sight rather than leading nowhere.
            Assert.DoesNotContain(item.GetVisualDescendants().OfType<Button>(),
                button => button.IsVisible);
        }
    }

    [AvaloniaFact]
    public async Task A_Configured_Key_Shows_Its_Summary_And_Opens_The_Editor_Through_编辑()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        viewModel.AddMappingCommand.Execute(null);
        viewModel.Editor.SelectedButton = GamepadButton.Back;
        viewModel.Editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        viewModel.Editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        await viewModel.CommitEditorCommand.ExecuteAsync(null);
        window.UpdateLayout();

        var leaderRow = SpecialKeysList(window)
            .GetVisualDescendants()
            .OfType<StackPanel>()
            .Single(panel => panel.DataContext is GlobalSpecialKeyItem { Kind: SpecialKeyKind.Leader });

        var texts = leaderRow.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains("Leader 键", texts);
        Assert.Contains(texts, text => text!.Contains("Back 键"));
        Assert.Contains(texts, text => text!.Contains("按下切换"));

        var edit = leaderRow.GetVisualDescendants().OfType<Button>().Single(button => button.IsVisible);
        Assert.Equal("编辑", edit.Content);

        Click(edit, window);

        Assert.True(viewModel.IsEditing);
        Assert.Equal(SpecialKeyKind.Leader, viewModel.Editor.ActiveSpecialKind);
        Assert.Equal(GamepadButton.Back, viewModel.Editor.SelectedButton);
    }

    [AvaloniaFact]
    public async Task A_Conflicting_Mapping_Row_Shows_The_Collision_Line()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        // B carries the default B -> Enter mapping; arming a leader on B puts a second entry
        // on the same button, and one press edge runs both mappings.
        viewModel.AddMappingCommand.Execute(null);
        viewModel.Editor.SelectedButton = GamepadButton.B;
        viewModel.Editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        viewModel.Editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        await viewModel.CommitEditorCommand.ExecuteAsync(null);
        window.UpdateLayout();

        var rows = MappingsList(window).GetVisualDescendants().OfType<MappingRowBorder>().ToList();
        var conflicting = rows.Where(row =>
                row.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                    text.Classes.Contains("conflictHint") && text.IsVisible))
            .ToList();

        // Both the leader's row and the Enter row it fires beside report the collision.
        Assert.Equal(2, conflicting.Count);
        foreach (var row in conflicting)
        {
            var hint = row.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Classes.Contains("conflictHint"));
            Assert.Contains("另一条映射", hint.Text!);
        }

        // An untouched row stays exactly as compact as before: no hint line at all.
        var plain = rows.Single(row =>
            row.DataContext is MappingRowViewModel { Entry.Source: ButtonBinding { Button: GamepadButton.A } });
        Assert.DoesNotContain(plain.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Classes.Contains("conflictHint") && text.IsVisible);
    }

    [AvaloniaFact]
    public async Task The_Global_Special_Keys_Area_Reports_The_Collision_Beside_The_Key()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        viewModel.AddMappingCommand.Execute(null);
        viewModel.Editor.SelectedButton = GamepadButton.B;
        viewModel.Editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        viewModel.Editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        await viewModel.CommitEditorCommand.ExecuteAsync(null);
        window.UpdateLayout();

        var leaderRow = SpecialKeysList(window)
            .GetVisualDescendants()
            .OfType<StackPanel>()
            .Single(panel => panel.DataContext is GlobalSpecialKeyItem { Kind: SpecialKeyKind.Leader });

        var hint = leaderRow.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.IsVisible && text.Text!.Contains("另一条映射"));

        Assert.Contains("Enter", hint.Text!);
    }

    [AvaloniaFact]
    public async Task The_Editor_Reports_The_Collision_Live_While_Configuring()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var bRow = viewModel.Mappings.Single(row => row.Entry.Source == new ButtonBinding(GamepadButton.B));
        viewModel.EditMappingCommand.Execute(bRow);
        viewModel.Editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        viewModel.Editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        viewModel.Editor.DoubleClickEnabled = true;
        window.UpdateLayout();

        var panel = window.GetLogicalDescendants().OfType<MappingEditorPanel>().First();
        var hint = panel.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Name == "SpecialKeyConflictHint");

        Assert.True(hint.IsVisible);
        Assert.Contains("双击窗口", hint.Text!);
    }
}
