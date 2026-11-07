using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

public class MappingEngineTests
{
    private static GamepadProfile Profile(params MappingEntry[] mappings) => new()
    {
        Name = "test-profile",
        Mappings = mappings,
    };

    private static MappingEntry Button(GamepadButton button, MappingTarget target)
        => new() { Source = new ButtonBinding(button), Target = target };

    private static MappingEntry Stick(GamepadStick stick, MappingTarget target)
        => new() { Source = new StickBinding(stick), Target = target };

    private static readonly MouseStickSettings RawMouse = new()
    {
        DeadZone = 0f,
        MaxSpeed = 640f,
        Curve = ResponseCurve.Linear,
        CurveStrength = 0f,
        ResponseTime = 0f,
        InvertY = false,
    };

    [Fact]
    public void Key_Press_And_Release_Emit_Down_Then_Up()
    {
        var engine = new MappingEngine(Profile(Button(GamepadButton.B, new KeyPressTarget(GamepadKey.Enter))));

        var press = engine.Tick(GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0), 0.016f);
        var hold = engine.Tick(GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0), 0.016f);
        var release = engine.Tick(GamepadState.Neutral, 0.016f);

        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(press));
        Assert.Empty(hold);
        Assert.Equal(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(release));
    }

    [Fact]
    public void Combo_Taps_Modifiers_Around_The_Key_On_Press_Only()
    {
        var engine = new MappingEngine(Profile(Button(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C))));

        var press = engine.Tick(GamepadState.Create(GamepadButtonMask.X, 0, 0, 0, 0), 0.016f);
        var hold = engine.Tick(GamepadState.Create(GamepadButtonMask.X, 0, 0, 0, 0), 0.016f);
        var release = engine.Tick(GamepadState.Neutral, 0.016f);

        Assert.Equal(2, press.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.C, KeyModifiers.Ctrl), press[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.C, KeyModifiers.Ctrl), press[1]);
        Assert.Empty(hold);
        Assert.Empty(release);
    }

    [Fact]
    public void Mouse_Button_Press_And_Release()
    {
        var engine = new MappingEngine(Profile(Button(GamepadButton.RightBumper, new MouseButtonTarget(MouseButton.Right))));

        var press = engine.Tick(GamepadState.Create(GamepadButtonMask.RightBumper, 0, 0, 0, 0), 0.016f);
        var release = engine.Tick(GamepadState.Neutral, 0.016f);

        Assert.Equal(new MouseDownCommand(MouseButton.Right), Assert.Single(press));
        Assert.Equal(new MouseUpCommand(MouseButton.Right), Assert.Single(release));
    }

    [Fact]
    public void Stick_To_Mouse_Mapping_Moves_Pointer()
    {
        var engine = new MappingEngine(Profile(Stick(GamepadStick.Right, new MouseMoveTarget(RawMouse))));

        var commands = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), 1f / 64f);

        var move = Assert.IsType<MouseMoveCommand>(Assert.Single(commands));
        Assert.Equal(10, move.Dx);
        Assert.Equal(0, move.Dy);
    }

    [Fact]
    public void Stick_To_Wheel_Mapping_Scrolls()
    {
        var engine = new MappingEngine(Profile(Stick(GamepadStick.Left, new WheelScrollTarget(new ScrollStickSettings
        {
            DeadZone = 0f,
            MaxTicksPerSecond = 64f,
            Curve = ResponseCurve.Linear,
            CurveStrength = 0f,
            ResponseTime = 0f,
        }))));

        var commands = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 1, 0, 0), 1f / 64f);

        var wheel = Assert.IsType<WheelCommand>(Assert.Single(commands));
        Assert.Equal(1, wheel.VerticalNotches);
    }

    [Fact]
    public void Disabled_Mappings_Are_Ignored()
    {
        var engine = new MappingEngine(Profile(
            Button(GamepadButton.B, new KeyPressTarget(GamepadKey.Enter)) with { Enabled = false }));

        var commands = engine.Tick(GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0), 0.016f);

        Assert.Empty(commands);
    }

    [Fact]
    public void Multiple_Buttons_In_One_Tick_All_Fire()
    {
        var engine = new MappingEngine(Profile(
            Button(GamepadButton.B, new KeyPressTarget(GamepadKey.Enter)),
            Button(GamepadButton.A, new KeyPressTarget(GamepadKey.Escape))));

        var commands = engine.Tick(GamepadState.Create(GamepadButtonMask.A | GamepadButtonMask.B, 0, 0, 0, 0), 0.016f);

        Assert.Equal(2, commands.Count);
        Assert.Contains(commands, c => c == new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None));
        Assert.Contains(commands, c => c == new KeyDownCommand(GamepadKey.Escape, KeyModifiers.None));
    }

    [Fact]
    public void Precision_Button_Slows_The_Mapped_Stick()
    {
        var engine = new MappingEngine(Profile(Stick(GamepadStick.Right, new MouseMoveTarget(RawMouse with
        {
            PrecisionScale = 0.5f,
            PrecisionButton = GamepadButton.LeftStick,
        }))));

        var fast = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), 1f / 64f);
        var slow = engine.Tick(GamepadState.Create(GamepadButtonMask.LeftStick, 0, 0, 1, 0), 1f / 64f);

        Assert.Equal(10, Assert.IsType<MouseMoveCommand>(Assert.Single(fast)).Dx);
        Assert.Equal(5, Assert.IsType<MouseMoveCommand>(Assert.Single(slow)).Dx);
    }

    [Fact]
    public void Leader_Press_Mode_Toggles_Between_SlotA_And_SlotB_On_Press_Edges()
    {
        var engine = new MappingEngine(Profile(
            Button(GamepadButton.Back, new SpecialKeyTarget(SpecialKeyKind.Leader, SwitchMode: LeaderSwitchMode.Press))));

        var firstPress = engine.Tick(GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0), 0.016f);
        var release = engine.Tick(GamepadState.Neutral, 0.016f);
        var secondPress = engine.Tick(GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0), 0.016f);

        Assert.Equal(new SwitchPresetCommand(2), Assert.Single(firstPress));
        Assert.Empty(release);
        Assert.Equal(new SwitchPresetCommand(1), Assert.Single(secondPress));
    }

    [Fact]
    public void Leader_Hold_Mode_Activates_SlotB_While_Held_And_Returns_To_SlotA_On_Release()
    {
        var engine = new MappingEngine(Profile(
            Button(GamepadButton.Back, new SpecialKeyTarget(SpecialKeyKind.Leader, SwitchMode: LeaderSwitchMode.Hold))));

        var press = engine.Tick(GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0), 0.016f);
        var hold = engine.Tick(GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0), 0.016f);
        var release = engine.Tick(GamepadState.Neutral, 0.016f);

        Assert.Equal(new SwitchPresetCommand(2), Assert.Single(press));
        Assert.Empty(hold);
        Assert.Equal(new SwitchPresetCommand(1), Assert.Single(release));
    }

    [Fact]
    public void Precision_Key_From_A_Special_Key_Mapping_Slows_The_Stick()
    {
        var engine = new MappingEngine(Profile(
            Stick(GamepadStick.Right, new MouseMoveTarget(RawMouse with { PrecisionScale = 0.5f })),
            Button(GamepadButton.LeftStick, new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Hold))));

        var fast = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), 1f / 64f);
        var slow = engine.Tick(GamepadState.Create(GamepadButtonMask.LeftStick, 0, 0, 1, 0), 1f / 64f);
        var released = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), 1f / 64f);

        Assert.Equal(10, Assert.IsType<MouseMoveCommand>(Assert.Single(fast)).Dx);
        Assert.Equal(5, Assert.IsType<MouseMoveCommand>(Assert.Single(slow)).Dx);
        Assert.Equal(10, Assert.IsType<MouseMoveCommand>(Assert.Single(released)).Dx);
    }

    [Fact]
    public void Default_Migration_Preserves_Precision_Behavior()
    {
        var legacy = DefaultProfileLibrary.CreateForLayout(GamepadLayout.Xbox) with { SchemaVersion = 1 };
        var migrated = ProfileMigration.Migrate(legacy);

        var fast = GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0);
        var slow = GamepadState.Create(GamepadButtonMask.LeftStick, 0, 0, 1, 0);

        var legacyFast = new MappingEngine(legacy).Tick(fast, 1f / 64f);
        var legacySlow = new MappingEngine(legacy).Tick(slow, 1f / 64f);
        var migratedFast = new MappingEngine(migrated).Tick(fast, 1f / 64f);
        var migratedSlow = new MappingEngine(migrated).Tick(slow, 1f / 64f);

        Assert.Equal(legacyFast, migratedFast);
        Assert.Equal(legacySlow, migratedSlow);
        Assert.NotEqual(legacyFast, legacySlow);
    }

    [Fact]
    public void Empty_Profile_Produces_No_Commands()
    {
        var engine = new MappingEngine(Profile());

        var commands = engine.Tick(GamepadState.Create(GamepadButtonMask.A | GamepadButtonMask.B, 1, 1, 1, 1), 0.016f);

        Assert.Empty(commands);
    }

    [Fact]
    public void Reset_Clears_Button_Edge_State()
    {
        var engine = new MappingEngine(Profile(Button(GamepadButton.B, new KeyPressTarget(GamepadKey.Enter))));

        engine.Tick(GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0), 0.016f);
        engine.Reset();

        // after reset the still-held button counts as a fresh press edge
        var commands = engine.Tick(GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0), 0.016f);

        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(commands));
    }

    [Fact]
    public void Extra_Button_Press_And_Release_Emit_Down_Then_Up()
    {
        var engine = new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Extra, 305),
                Target = new KeyPressTarget(GamepadKey.F13),
            }));

        var press = engine.Tick(new ExtraButtonState(305), 0.016f);
        var hold = engine.Tick(new ExtraButtonState(305), 0.016f);
        var release = engine.Tick(GamepadState.Neutral, 0.016f);

        Assert.Equal(new KeyDownCommand(GamepadKey.F13, KeyModifiers.None), Assert.Single(press));
        Assert.Empty(hold);
        Assert.Equal(new KeyUpCommand(GamepadKey.F13, KeyModifiers.None), Assert.Single(release));
    }

    [Fact]
    public void Extra_Buttons_Are_Matched_By_Raw_Code_Not_By_Position()
    {
        var engine = new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Extra, 305),
                Target = new KeyPressTarget(GamepadKey.F13),
            }));

        var other = engine.Tick(new ExtraButtonState(306), 0.016f);

        Assert.Empty(other);
    }

    [Fact]
    public void Reset_Clears_Extra_Button_Edge_State()
    {
        var engine = new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Extra, 305),
                Target = new KeyPressTarget(GamepadKey.F13),
            }));

        engine.Tick(new ExtraButtonState(305), 0.016f);
        engine.Reset();

        var commands = engine.Tick(new ExtraButtonState(305), 0.016f);

        Assert.Equal(new KeyDownCommand(GamepadKey.F13, KeyModifiers.None), Assert.Single(commands));
    }

    private sealed record ExtraButtonState(params int[] Codes) : IGamepadState
    {
        public GamepadButtonMask PressedButtons => GamepadButtonMask.None;

        public float LeftStickX => 0f;

        public float LeftStickY => 0f;

        public float RightStickX => 0f;

        public float RightStickY => 0f;

        public IReadOnlyList<int> ExtraButtons => Codes;
    }
}
