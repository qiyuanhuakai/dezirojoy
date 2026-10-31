using System.Numerics;
using DeziroJoy.Core.Input;
using Xunit;

namespace DeziroJoy.Core.Tests.Input;

public class GamepadInputModelTests
{
    [Fact]
    public void Neutral_State_Has_No_Pressed_Buttons_And_Centered_Axes()
    {
        var state = GamepadState.Neutral;

        Assert.Equal(GamepadButtonMask.None, state.PressedButtons);
        Assert.Equal(0f, state.LeftStickX);
        Assert.Equal(0f, state.LeftStickY);
        Assert.Equal(0f, state.RightStickX);
        Assert.Equal(0f, state.RightStickY);
    }

    [Fact]
    public void Create_Clamps_Stick_Axes_To_Unit_Range()
    {
        var state = GamepadState.Create(GamepadButtonMask.None, 2f, -2f, 0.5f, -0.5f);

        Assert.Equal(1f, state.LeftStickX);
        Assert.Equal(-1f, state.LeftStickY);
        Assert.Equal(0.5f, state.RightStickX);
        Assert.Equal(-0.5f, state.RightStickY);
    }

    [Fact]
    public void IsPressed_Reflects_Button_Mask()
    {
        var state = GamepadState.Create(GamepadButtonMask.A | GamepadButtonMask.DPadUp, 0, 0, 0, 0);

        Assert.True(state.IsPressed(GamepadButton.A));
        Assert.True(state.IsPressed(GamepadButton.DPadUp));
        Assert.False(state.IsPressed(GamepadButton.B));
    }

    [Fact]
    public void Every_Button_Maps_To_A_Unique_Mask_Bit()
    {
        var buttons = Enum.GetValues<GamepadButton>();
        var masks = buttons.Select(b => b.ToMask()).ToArray();

        Assert.All(masks, mask => Assert.NotEqual(GamepadButtonMask.None, mask));
        Assert.Equal(buttons.Length, masks.Distinct().Count());
    }

    [Fact]
    public void Mask_Add_Remove_And_Has_Roundtrip_For_Every_Button()
    {
        var all = Enum.GetValues<GamepadButton>()
            .Aggregate(GamepadButtonMask.None, (acc, button) => acc.Add(button));

        foreach (var button in Enum.GetValues<GamepadButton>())
        {
            Assert.True(all.Has(button), $"{button} should be present in the full mask");
        }

        Assert.Equal(Enum.GetValues<GamepadButton>().Length, BitOperations.PopCount((uint)all));

        var withoutA = all.Remove(GamepadButton.A);
        Assert.False(withoutA.Has(GamepadButton.A));
        Assert.True(withoutA.Has(GamepadButton.B));
    }

    [Fact]
    public void Neutral_Mask_Contains_No_Buttons()
    {
        var empty = GamepadButtonMask.None;

        Assert.All(Enum.GetValues<GamepadButton>(), button => Assert.False(empty.Has(button)));
    }
}
