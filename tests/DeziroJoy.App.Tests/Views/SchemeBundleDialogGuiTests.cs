using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// The scheme import preview dialog: it shows what the import will replace, applies it as one
/// atomic operation on 确认导入 and offers the one-level undo afterwards. The view model is
/// driven through its own commands; the dialog is a window over it, so these tests stand in
/// for the settings panel by driving the view model and reading the dialog back.
/// </summary>
public sealed class SchemeBundleDialogGuiTests : IDisposable
{
    private readonly string _directory;

    public SchemeBundleDialogGuiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-scheme-dialog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static (MainWindowViewModel Vm, EngineHost Host, FakeGamepadProvider Provider, InMemoryProfileStore Store) Connected()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var vm = new MainWindowViewModel(host, store);

        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        SampleHelper.Sample(vm, host);

        return (vm, host, provider, store);
    }

    private static SchemeBundle TwoPresetBundle()
    {
        var baseProfile = new GamepadProfile
        {
            Name = "导入的主配置",
            Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.B), Target = new KeyPressTarget(GamepadKey.Enter) }],
        };

        var secondProfile = new GamepadProfile
        {
            Name = "导入的第二套",
            Mappings = [new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.Escape) }],
        };

        return new SchemeBundle
        {
            Name = "导入的方案",
            AppVersion = "9.9.9-test",
            Notes = "使用说明。",
            Presets =
            [
                new SchemePreset { Slot = 1, Profile = baseProfile, ActionNames = SchemeBundleFactory.ActionNamesOf(baseProfile) },
                new SchemePreset { Slot = 2, Profile = secondProfile, ActionNames = SchemeBundleFactory.ActionNamesOf(secondProfile) },
            ],
            SpecialKeys = [],
            Layers = [],
        };
    }

    private string WriteBundle()
    {
        var path = Path.Combine(_directory, "incoming.json");
        File.WriteAllText(path, SchemeBundleCodec.Serialize(TwoPresetBundle()));
        return path;
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

    [AvaloniaFact]
    public void The_Dialog_Shows_What_The_Import_Will_Replace()
    {
        var (vm, _, _, _) = Connected();
        vm.ImportSchemeFrom(WriteBundle());
        var dialog = new SchemeBundleDialog(vm);

        try
        {
            dialog.Show();
            dialog.UpdateLayout();

            var rows = dialog.FindControl<ItemsControl>("SlotRows")!;
            Assert.Equal(4, rows.ItemCount!);

            var texts = rows.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
            Assert.Contains(texts, text => text!.Contains("槽位 1"));
            Assert.Contains(texts, text => text!.Contains("将替换为「导入的主配置」"));
            Assert.Contains(texts, text => text!.Contains("方案不包含此槽位"));

            // The confirm affordance is the one on screen before anything is applied.
            Assert.True(dialog.FindControl<Button>("ConfirmSchemeButton")!.IsVisible);
            Assert.False(dialog.FindControl<Button>("UndoSchemeButton")!.IsVisible);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Confirm_Applies_The_Scheme_And_The_Dialog_Offers_The_Undo()
    {
        var (vm, _, _, store) = Connected();
        vm.ImportSchemeFrom(WriteBundle());
        var dialog = new SchemeBundleDialog(vm);

        try
        {
            dialog.Show();
            dialog.UpdateLayout();

            Click(dialog.FindControl<Button>("ConfirmSchemeButton")!, dialog);

            Assert.True(vm.SchemeImportApplied);
            Assert.Equal([1, 2], new PresetStore(store).SlotsInUse("xinput:0"));
            Assert.Equal("导入的主配置", store.Load("xinput:0")!.Name);

            // The result state replaces the confirm state: the undo is what the dialog offers.
            Assert.False(dialog.FindControl<Button>("ConfirmSchemeButton")!.IsVisible);
            Assert.True(dialog.FindControl<Button>("UndoSchemeButton")!.IsVisible);
            Assert.True(dialog.FindControl<Button>("UndoSchemeButton")!.IsEnabled);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Undo_Restores_The_Replaced_Slots_And_Closes_The_Dialog()
    {
        var (vm, _, _, store) = Connected();
        var presets = new PresetStore(store);
        var before = presets.LoadSlot("xinput:0", 1)!.Name;
        vm.ImportSchemeFrom(WriteBundle());
        var dialog = new SchemeBundleDialog(vm);

        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            Click(dialog.FindControl<Button>("ConfirmSchemeButton")!, dialog);
            Click(dialog.FindControl<Button>("UndoSchemeButton")!, dialog);

            Assert.Equal([1, 2, 3, 4], presets.SlotsInUse("xinput:0"));
            Assert.Equal(before, presets.LoadSlot("xinput:0", 1)!.Name);
            Assert.False(vm.CanUndoSchemeImport);
            Assert.False(vm.IsSchemePreviewOpen);
        }
        finally
        {
            if (dialog.IsVisible)
            {
                dialog.Close();
            }
        }
    }

    [AvaloniaFact]
    public void Cancel_Closes_The_Dialog_Without_Touching_The_Device()
    {
        var (vm, _, _, store) = Connected();
        var before = store.Load("xinput:0")!.Name;
        vm.ImportSchemeFrom(WriteBundle());
        var dialog = new SchemeBundleDialog(vm);

        try
        {
            dialog.Show();
            dialog.UpdateLayout();

            Click(dialog.FindControl<Button>("CancelSchemeButton")!, dialog);

            Assert.False(vm.IsSchemePreviewOpen);
            Assert.Equal(before, store.Load("xinput:0")!.Name);
        }
        finally
        {
            if (dialog.IsVisible)
            {
                dialog.Close();
            }
        }
    }
}
