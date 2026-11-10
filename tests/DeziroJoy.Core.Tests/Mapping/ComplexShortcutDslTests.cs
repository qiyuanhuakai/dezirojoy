using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Mapping;

/// <summary>
/// The hand-written complex-shortcut DSL: one step per line, fixed key vocabulary with
/// friendly aliases, bounded durations and step counts, and parse errors that name the
/// line and column in Chinese so a hand-edited profile can be fixed without counting
/// characters.
/// </summary>
public class ComplexShortcutDslTests
{
    [Fact]
    public void Parses_The_User_Example()
    {
        var steps = ComplexShortcutDsl.Parse("hold Alt\nevery 1s tap Tab");

        Assert.Equal(2, steps.Count);
        Assert.Equal(new ComplexShortcutHoldStep(GamepadKey.LeftAlt), steps[0]);
        Assert.Equal(new ComplexShortcutEveryStep(1d, GamepadKey.Tab), steps[1]);
    }

    [Fact]
    public void Parses_Every_Step_Kind()
    {
        var steps = ComplexShortcutDsl.Parse("tap A\nhold Shift\nrelease Shift\nwait 0.5s\nevery 200ms tap Space");

        Assert.Equal(
            [
                new ComplexShortcutTapStep(GamepadKey.A),
                new ComplexShortcutHoldStep(GamepadKey.LeftShift),
                new ComplexShortcutReleaseStep(GamepadKey.LeftShift),
                new ComplexShortcutWaitStep(0.5d),
                new ComplexShortcutEveryStep(0.2d, GamepadKey.Space),
            ],
            steps);
    }

    [Fact]
    public void Durations_Accept_Bare_Seconds_Suffixes_And_Milliseconds()
    {
        Assert.Equal(new ComplexShortcutWaitStep(1.5d), Assert.Single(ComplexShortcutDsl.Parse("wait 1.5")));
        Assert.Equal(new ComplexShortcutWaitStep(2d), Assert.Single(ComplexShortcutDsl.Parse("wait 2s")));
        Assert.Equal(new ComplexShortcutWaitStep(0.5d), Assert.Single(ComplexShortcutDsl.Parse("wait 500ms")));
        Assert.Equal(new ComplexShortcutEveryStep(0.25d, GamepadKey.Tab), Assert.Single(ComplexShortcutDsl.Parse("every 250ms tap Tab")));
    }

    [Fact]
    public void Key_Names_Are_Case_Insensitive_And_Accept_Aliases()
    {
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.Tab), Assert.Single(ComplexShortcutDsl.Parse("tap TAB")));
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.LeftAlt), Assert.Single(ComplexShortcutDsl.Parse("tap alt")));
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.LeftCtrl), Assert.Single(ComplexShortcutDsl.Parse("tap Ctrl")));
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.Escape), Assert.Single(ComplexShortcutDsl.Parse("tap esc")));
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.ArrowUp), Assert.Single(ComplexShortcutDsl.Parse("tap up")));
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.D0), Assert.Single(ComplexShortcutDsl.Parse("tap 0")));
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.F13), Assert.Single(ComplexShortcutDsl.Parse("tap F13")));
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.Numpad5), Assert.Single(ComplexShortcutDsl.Parse("tap numpad5")));
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.BracketLeft), Assert.Single(ComplexShortcutDsl.Parse("tap [")));
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.Minus), Assert.Single(ComplexShortcutDsl.Parse("tap -")));
    }

    [Fact]
    public void Blank_Lines_And_Comments_Are_Skipped()
    {
        var steps = ComplexShortcutDsl.Parse("# alt-tab macro\n\nhold Alt   # keep it down\n\nevery 1s tap Tab\n");

        Assert.Equal(2, steps.Count);
        Assert.Equal(new ComplexShortcutHoldStep(GamepadKey.LeftAlt), steps[0]);
        Assert.Equal(new ComplexShortcutEveryStep(1d, GamepadKey.Tab), steps[1]);
    }

    [Fact]
    public void Every_Registers_A_Timer_Without_Blocking_Later_Steps()
    {
        var steps = ComplexShortcutDsl.Parse("every 1s tap Tab\ntap Enter");

        Assert.Equal(2, steps.Count);
        Assert.Equal(new ComplexShortcutEveryStep(1d, GamepadKey.Tab), steps[0]);
        Assert.Equal(new ComplexShortcutTapStep(GamepadKey.Enter), steps[1]);
    }

    [Theory]
    [InlineData("frobnicate Tab", "未知指令")]
    [InlineData("tap Fooo", "未知按键")]
    [InlineData("wait 0.01s", "超出范围")]
    [InlineData("wait 61s", "超出范围")]
    [InlineData("every 0.01s tap Tab", "超出范围")]
    [InlineData("wait banana", "无效时长")]
    [InlineData("wait", "缺少参数")]
    [InlineData("tap A B", "参数过多")]
    [InlineData("every 1s hold Tab", "every 之后应为 tap")]
    [InlineData("every 1s tap", "缺少参数")]
    public void Malformed_Lines_Fail_With_A_Chinese_Reason(string script, string reason)
    {
        var exception = Assert.Throws<ComplexShortcutParseException>(() => ComplexShortcutDsl.Parse(script));

        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_Point_At_The_Line_And_Column_Of_The_Offending_Token()
    {
        var exception = Assert.Throws<ComplexShortcutParseException>(() =>
            ComplexShortcutDsl.Parse("hold Alt\ntap Fooo"));

        Assert.Equal(2, exception.Line);
        Assert.Equal(5, exception.Column);
        Assert.Contains("第 2 行第 5 列", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Empty_Script_Is_Rejected()
    {
        Assert.Throws<ComplexShortcutParseException>(() => ComplexShortcutDsl.Parse(""));
        Assert.Throws<ComplexShortcutParseException>(() => ComplexShortcutDsl.Parse("# only a comment\n"));
    }

    [Fact]
    public void A_Program_Is_Capped_At_The_Max_Step_Count()
    {
        var script = string.Join('\n', Enumerable.Range(0, ComplexShortcutDsl.MaxSteps).Select(_ => "tap A"));
        Assert.Equal(ComplexShortcutDsl.MaxSteps, ComplexShortcutDsl.Parse(script).Count);

        var oneMore = script + "\ntap A";
        var exception = Assert.Throws<ComplexShortcutParseException>(() => ComplexShortcutDsl.Parse(oneMore));

        Assert.Equal(ComplexShortcutDsl.MaxSteps + 1, exception.Line);
        Assert.Contains("步骤过多", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Null_Script_Is_Rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ComplexShortcutDsl.Parse(null!));
    }
}
