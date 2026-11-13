using DeziroJoy.App.Ipc;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The layer hint overlay: while a layer leader is held, the screen edge names what each
/// button DOES on that layer. The rows come from the daemon's snapshot - the layer it is
/// really running, not the profile body - resolved against the profile the snapshot carries,
/// and they must appear and disappear with the layer itself.
/// </summary>
public sealed class LayerHintViewModelTests
{
    private static GamepadProfile Profile(params MappingEntry[] mappings)
        => new() { Name = "layer-profile", Mappings = mappings };

    private static MappingEntry Base(GamepadButton button, MappingTarget target)
        => new() { Source = new ButtonBinding(button), Target = target };

    private static MappingEntry OnLayer(GamepadButton button, MappingTarget target, bool enabled = true)
        => new() { Source = new ButtonBinding(button), Target = target, Layer = 2, Enabled = enabled };

    /// <summary>A snapshot carrying one device's layer state; the profile body rides along empty.</summary>
    private static StateSnapshot Snapshot(params ActiveLayerSnapshot[] layers)
        => new(1, 1, Running: true, MappingEnabled: true, 4242, "high", "layer-profile", null, 0, 0, true)
        {
            Layers = layers,
        };

    [Fact]
    public void The_Hint_Names_Every_Mapping_The_Layer_Itself_Contributes()
    {
        var copy = Guid.NewGuid();
        var paste = Guid.NewGuid();
        var undo = Guid.NewGuid();
        var save = Guid.NewGuid();
        var profile = Profile(
            Base(GamepadButton.RightTrigger, new MouseButtonTarget(MouseButton.Left)),
            OnLayer(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C)) with { Id = copy },
            OnLayer(GamepadButton.Y, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.V)) with { Id = paste },
            OnLayer(GamepadButton.A, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.Z)) with { Id = undo },
            OnLayer(GamepadButton.B, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.S)) with { Id = save });
        var hint = new LayerHintViewModel();

        hint.Update(profile, Snapshot(new ActiveLayerSnapshot(
            "pad-1",
            2,
            [
                new EffectiveMappingSnapshot(Guid.NewGuid(), 0),
                new EffectiveMappingSnapshot(copy, 2),
                new EffectiveMappingSnapshot(paste, 2),
                new EffectiveMappingSnapshot(undo, 2),
                new EffectiveMappingSnapshot(save, 2),
            ])));

        // The base drag mapping the layer inherits is NOT one of the layer's own: the hint
        // names what the layer changes, not everything the device is running.
        Assert.True(hint.IsActive);
        Assert.Equal("临时层 2", hint.LayerTitle);
        Assert.Equal(
            [
                new LayerHintRow("X", "复制"),
                new LayerHintRow("Y", "粘贴"),
                new LayerHintRow("A", "撤销"),
                new LayerHintRow("B", "保存"),
            ],
            hint.Rows);
    }

    [Fact]
    public void The_Base_Layer_Alone_Keeps_The_Hint_Hidden()
    {
        var profile = Profile(Base(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C)));
        var hint = new LayerHintViewModel();

        hint.Update(profile, Snapshot(new ActiveLayerSnapshot("pad-1", 0, [new EffectiveMappingSnapshot(profile.Mappings[0].Id, 0)])));

        Assert.False(hint.IsActive);
        Assert.Empty(hint.Rows);
        Assert.Equal(string.Empty, hint.LayerTitle);
    }

    [Fact]
    public void Releasing_The_Layer_Hides_The_Hint_And_Clears_Its_Rows()
    {
        var copy = Guid.NewGuid();
        var profile = Profile(OnLayer(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C)) with { Id = copy });
        var hint = new LayerHintViewModel();

        hint.Update(profile, Snapshot(new ActiveLayerSnapshot("pad-1", 2, [new EffectiveMappingSnapshot(copy, 2)])));
        Assert.True(hint.IsActive);

        hint.Update(profile, Snapshot(new ActiveLayerSnapshot("pad-1", 0, [new EffectiveMappingSnapshot(copy, 0)])));

        Assert.False(hint.IsActive);
        Assert.Empty(hint.Rows);
        Assert.Equal(string.Empty, hint.LayerTitle);
    }

    [Fact]
    public void Losing_The_Daemon_Hides_The_Hint()
    {
        var profile = Profile(OnLayer(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C)));
        var hint = new LayerHintViewModel();

        hint.Update(profile, Snapshot(new ActiveLayerSnapshot("pad-1", 2, [new EffectiveMappingSnapshot(profile.Mappings[0].Id, 2)])));
        Assert.True(hint.IsActive);

        hint.Update(profile, null);

        Assert.False(hint.IsActive);
        Assert.Empty(hint.Rows);
    }

    [Fact]
    public void A_Disabled_Layer_Mapping_Is_Not_Advertised()
    {
        var live = Guid.NewGuid();
        var off = Guid.NewGuid();
        var profile = Profile(
            OnLayer(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C)) with { Id = live },
            OnLayer(GamepadButton.Y, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.V), enabled: false) with { Id = off });
        var hint = new LayerHintViewModel();

        hint.Update(profile, Snapshot(new ActiveLayerSnapshot(
            "pad-1",
            2,
            [new EffectiveMappingSnapshot(live, 2), new EffectiveMappingSnapshot(off, 2)])));

        // A disabled mapping emits nothing, so the hint must not promise it: pressing Y
        // while the layer is held would do exactly nothing.
        Assert.Equal([new LayerHintRow("X", "复制")], hint.Rows);
    }

    [Fact]
    public void A_Layer_Whose_Mappings_Left_The_Profile_Shows_Nothing()
    {
        var profile = Profile(Base(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C)));
        var hint = new LayerHintViewModel();

        // A profile edit committed while the leader is held can retire the very mapping the
        // running layer still lists; the id no longer resolves, so the row cannot be named.
        hint.Update(profile, Snapshot(new ActiveLayerSnapshot("pad-1", 2, [new EffectiveMappingSnapshot(Guid.NewGuid(), 2)])));

        Assert.False(hint.IsActive);
        Assert.Empty(hint.Rows);
    }

    [Fact]
    public void An_Unknown_Profile_Names_The_Action_Not_The_Key_Combo()
    {
        var profile = Profile(OnLayer(GamepadButton.A, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.Q)));
        var hint = new LayerHintViewModel();

        hint.Update(profile, Snapshot(new ActiveLayerSnapshot("pad-1", 2, [new EffectiveMappingSnapshot(profile.Mappings[0].Id, 2)])));

        // Ctrl+Q has no universal meaning, so the honest row is the combo itself.
        Assert.Equal([new LayerHintRow("A", "Ctrl + Q")], hint.Rows);
    }

    [Fact]
    public void A_Stick_Layer_Mapping_Names_The_Stick()
    {
        var drag = Guid.NewGuid();
        var profile = Profile(
            new MappingEntry
            {
                Id = drag,
                Source = new StickBinding(GamepadStick.Left),
                Target = new MouseMoveTarget(new MouseStickSettings()),
                Layer = 2,
            });
        var hint = new LayerHintViewModel();

        hint.Update(profile, Snapshot(new ActiveLayerSnapshot("pad-1", 2, [new EffectiveMappingSnapshot(drag, 2)])));

        Assert.Equal([new LayerHintRow("左摇杆", "鼠标移动")], hint.Rows);
    }
}
