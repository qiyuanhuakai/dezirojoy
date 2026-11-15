using DeziroJoy.App.Ipc;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public sealed class LayerHintGestureTests
{
    [Fact]
    public void Hint_Lists_Double_Click_And_Long_Press_With_Click_Fallback_Semantics()
    {
        var id = Guid.NewGuid();
        var profile = new GamepadProfile
        {
            Name = "gestures",
            Mappings =
            [
                new MappingEntry
                {
                    Id = id,
                    Layer = 1,
                    Source = new ButtonBinding(GamepadButton.X)
                    {
                        Gesture = new ButtonGesture
                        {
                            DoubleClickWindow = 0.25f,
                            LongPressThreshold = 0.5f,
                            LongPressAction = new KeyComboTarget(KeyModifiers.Alt, GamepadKey.F4),
                        },
                    },
                    Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C),
                },
            ],
        };
        var snapshot = new StateSnapshot(1, 1, true, true, 1, null, "gestures", null, 0, 0, true)
        {
            Layers = [new ActiveLayerSnapshot("pad", 1, [new EffectiveMappingSnapshot(id, 1)])],
        };
        var hint = new LayerHintViewModel();

        hint.Update(profile, snapshot);

        var row = Assert.Single(hint.Rows);
        Assert.Equal("复制", row.Action);
        Assert.Equal("同单击（复制）", row.DoubleClickAction);
        Assert.Equal("关闭窗口", row.LongPressAction);
    }

    [Fact]
    public void Click_Repeat_Suppresses_The_Long_Press_Hint_Like_The_Engine()
    {
        var id = Guid.NewGuid();
        var profile = new GamepadProfile
        {
            Name = "repeat",
            Mappings =
            [
                new MappingEntry
                {
                    Id = id,
                    Layer = 1,
                    Source = new ButtonBinding(GamepadButton.A)
                    {
                        Gesture = new ButtonGesture
                        {
                            ClickRepeatInterval = 0.1f,
                            LongPressThreshold = 0.5f,
                            LongPressAction = new KeyPressTarget(GamepadKey.Escape),
                        },
                    },
                    Target = new KeyPressTarget(GamepadKey.Enter),
                },
            ],
        };
        var snapshot = new StateSnapshot(1, 1, true, true, 1, null, "repeat", null, 0, 0, true)
        {
            Layers = [new ActiveLayerSnapshot("pad", 1, [new EffectiveMappingSnapshot(id, 1)])],
        };
        var hint = new LayerHintViewModel();

        hint.Update(profile, snapshot);

        var row = Assert.Single(hint.Rows);
        Assert.False(row.HasDoubleClickAction);
        Assert.False(row.HasLongPressAction);
    }
}
