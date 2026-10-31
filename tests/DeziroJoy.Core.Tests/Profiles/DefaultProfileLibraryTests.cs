using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;
using Xunit;

namespace DeziroJoy.Core.Tests.Profiles;

public class DefaultProfileLibraryTests
{
    [Theory]
    [InlineData(GamepadLayout.Xbox)]
    [InlineData(GamepadLayout.DualShock)]
    [InlineData(GamepadLayout.Generic)]
    public void Every_Layout_Ships_The_Specified_Default_Mappings(GamepadLayout layout)
    {
        var profile = DefaultProfileLibrary.CreateForLayout(layout);

        Assert.Equal(layout, profile.Layout);
        Assert.Equal(10, profile.Mappings.Count);

        Assert.NotNull(SingleStickTarget<WheelScrollTarget>(profile, GamepadStick.Left));
        Assert.NotNull(SingleStickTarget<MouseMoveTarget>(profile, GamepadStick.Right));

        AssertKey(profile, GamepadButton.DPadUp, GamepadKey.ArrowUp);
        AssertKey(profile, GamepadButton.DPadDown, GamepadKey.ArrowDown);
        AssertKey(profile, GamepadButton.DPadLeft, GamepadKey.ArrowLeft);
        AssertKey(profile, GamepadButton.DPadRight, GamepadKey.ArrowRight);

        AssertKey(profile, GamepadButton.B, GamepadKey.Enter);
        AssertKey(profile, GamepadButton.A, GamepadKey.Escape);
        AssertCombo(profile, GamepadButton.X, KeyModifiers.Ctrl, GamepadKey.C);
        AssertCombo(profile, GamepadButton.Y, KeyModifiers.Ctrl, GamepadKey.V);
    }

    [Fact]
    public void CreateDefault_Uses_Detected_Layout_And_Stamps_Device_Id()
    {
        var descriptor = new GamepadDescriptor("dev-42", "Sony DualSense Wireless Controller");

        var profile = DefaultProfileLibrary.CreateDefault(descriptor);

        Assert.Equal(GamepadLayout.DualShock, profile.Layout);
        Assert.Equal("dev-42", profile.DeviceId);
        Assert.Equal(10, profile.Mappings.Count);
        Assert.False(string.IsNullOrWhiteSpace(profile.Name));
    }

    [Fact]
    public void Every_Mapping_Is_Enabled_And_Sources_Are_Unique()
    {
        var profile = DefaultProfileLibrary.CreateForLayout(GamepadLayout.Xbox);

        Assert.All(profile.Mappings, mapping => Assert.True(mapping.Enabled));
        Assert.Equal(profile.Mappings.Count, profile.Mappings.Select(m => m.Source).Distinct().Count());
    }

    [Fact]
    public void Built_In_Profiles_Roundtrip_Through_Json()
    {
        var profile = DefaultProfileLibrary.CreateDefault(new GamepadDescriptor("dev-1", "Xbox Wireless Controller"));

        var json = ProfileSerializer.Serialize(profile);
        var back = ProfileSerializer.Deserialize(json);

        Assert.Equal(json, ProfileSerializer.Serialize(back));
        Assert.Equal("dev-1", back.DeviceId);
    }

    [Fact]
    public void Xbox_Default_Profile_Works_With_The_Engine_End_To_End()
    {
        var profile = DefaultProfileLibrary.CreateForLayout(GamepadLayout.Xbox);
        var engine = new MappingEngine(profile);

        var bPress = engine.Tick(GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0), 1f / 64f);
        var bRelease = engine.Tick(GamepadState.Neutral, 1f / 64f);
        var xPress = engine.Tick(GamepadState.Create(GamepadButtonMask.X, 0, 0, 0, 0), 1f / 64f);

        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(bPress));
        Assert.Equal(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(bRelease));
        Assert.Equal(2, xPress.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.C, KeyModifiers.Ctrl), xPress[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.C, KeyModifiers.Ctrl), xPress[1]);

        WheelCommand? wheelCommand = null;
        for (var i = 0; i < 40; i++)
        {
            var commands = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 1, 0, 0), 1f / 64f);
            wheelCommand = commands.OfType<WheelCommand>().FirstOrDefault() ?? wheelCommand;
        }

        Assert.NotNull(wheelCommand);
        Assert.Equal(1, wheelCommand.VerticalNotches);
    }

    private static MappingEntry Single(GamepadProfile profile, GamepadButton button)
        => Assert.Single(
            profile.Mappings,
            m => m.Source is ButtonBinding binding && binding.Button == button);

    private static T? SingleStickTarget<T>(GamepadProfile profile, GamepadStick stick)
        where T : MappingTarget
        => profile.Mappings
            .Where(m => m.Source is StickBinding binding && binding.Stick == stick)
            .Select(m => m.Target)
            .OfType<T>()
            .SingleOrDefault();

    private static void AssertKey(GamepadProfile profile, GamepadButton button, GamepadKey key)
    {
        var target = Assert.IsType<KeyPressTarget>(Single(profile, button).Target);
        Assert.Equal(key, target.Key);
    }

    private static void AssertCombo(GamepadProfile profile, GamepadButton button, KeyModifiers modifiers, GamepadKey key)
    {
        var target = Assert.IsType<KeyComboTarget>(Single(profile, button).Target);
        Assert.Equal(modifiers, target.Modifiers);
        Assert.Equal(key, target.Key);
    }
}
