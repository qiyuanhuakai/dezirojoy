using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Mapping;

/// <summary>
/// The foreground rule table: one program in front in, one decision out. Matching is by
/// process name, priority decides which of several matches wins, a miss falls back to the
/// desktop default, and the decision always carries the reason the UI quotes. The fullscreen
/// condition is auxiliary - a rule that asks for it must never be silently disabled by a
/// platform that cannot detect it.
/// </summary>
public sealed class ForegroundRuleTests
{
    private static ForegroundRule Rule(
        string process,
        int priority = 0,
        ForegroundAction action = ForegroundAction.Profile,
        int slot = 1,
        bool enabled = true,
        bool fullscreenOnly = false)
        => new()
        {
            ProcessName = process,
            Priority = priority,
            Action = action,
            Slot = slot,
            Enabled = enabled,
            FullscreenOnly = fullscreenOnly,
        };

    private static ForegroundContext Program(string name, bool? fullscreen = false)
        => new(name, fullscreen);

    [Fact]
    public void A_Rule_Matches_Its_Own_Process()
    {
        var rule = Rule("chrome");

        Assert.True(rule.Matches(Program("chrome")));
    }

    [Fact]
    public void Process_Names_Match_Case_Insensitively_And_Ignore_The_Exe_Suffix()
    {
        var rule = Rule("Chrome.exe");

        Assert.True(rule.Matches(Program("CHROME")));
        Assert.True(rule.Matches(Program("chrome.exe")));
        Assert.True(rule.Matches(Program("  Chrome.EXE  ")));
    }

    [Fact]
    public void A_Rule_Does_Not_Match_Another_Process()
    {
        var rule = Rule("chrome");

        Assert.False(rule.Matches(Program("firefox")));
        Assert.False(rule.Matches(Program("chromium")));
    }

    [Fact]
    public void A_Disabled_Rule_Never_Matches()
    {
        Assert.False(Rule("chrome", enabled: false).Matches(Program("chrome")));
    }

    [Fact]
    public void No_Rule_Matches_The_Desktop()
    {
        // The desktop is not a program: a rule that names a process, and even the wildcard,
        // must leave it alone so the default profile is what runs there.
        Assert.False(Rule("*").Matches(ForegroundContext.Empty));
        Assert.False(Rule("chrome").Matches(new ForegroundContext(null, true)));
        Assert.False(Rule("chrome").Matches(new ForegroundContext("   ", true)));
    }

    [Fact]
    public void The_Wildcard_Rule_Matches_Every_Program()
    {
        var rule = Rule("*", priority: 5, action: ForegroundAction.NativeGamepad);

        Assert.True(rule.Matches(Program("anything")));
        Assert.True(rule.Matches(Program("game.exe")));
    }

    [Fact]
    public void Fullscreen_Only_Matches_A_Fullscreen_Program_And_Not_A_Windowed_One()
    {
        var rule = Rule("game", fullscreenOnly: true);

        Assert.True(rule.Matches(Program("game", fullscreen: true)));
        Assert.False(rule.Matches(Program("game", fullscreen: false)));
    }

    [Fact]
    public void Fullscreen_Only_Still_Matches_Where_Fullscreen_Cannot_Be_Detected()
    {
        // The condition is auxiliary and must never silently disable a rule: Wayland cannot
        // answer the question at all, and a rule there has to keep working.
        var rule = Rule("game", fullscreenOnly: true);

        Assert.True(rule.Matches(Program("game", fullscreen: null)));
    }

    [Fact]
    public void The_Highest_Priority_Match_Wins()
    {
        var settings = new ForegroundSettings
        {
            Rules =
            [
                Rule("chrome", priority: 1, slot: 2),
                Rule("chrome", priority: 9, slot: 3),
                Rule("chrome", priority: 4, slot: 4),
            ],
            DefaultSlot = 1,
        };

        var decision = ForegroundRules.Evaluate(settings, Program("chrome"));

        Assert.Equal(ForegroundDecisionKind.Profile, decision.Kind);
        Assert.Equal(3, decision.Slot);
    }

    [Fact]
    public void A_Priority_Tie_Keeps_The_First_Rule_In_List_Order()
    {
        var settings = new ForegroundSettings
        {
            Rules = [Rule("chrome", priority: 7, slot: 2), Rule("chrome", priority: 7, slot: 5)],
        };

        Assert.Equal(2, ForegroundRules.Evaluate(settings, Program("chrome")).Slot);
    }

    [Fact]
    public void A_Lower_Priority_Rule_Still_Wins_When_The_Higher_One_Is_Disabled()
    {
        var settings = new ForegroundSettings
        {
            Rules = [Rule("chrome", priority: 9, slot: 3, enabled: false), Rule("chrome", priority: 1, slot: 2)],
        };

        Assert.Equal(2, ForegroundRules.Evaluate(settings, Program("chrome")).Slot);
    }

    [Fact]
    public void No_Match_Falls_Back_To_The_Desktop_Default_Profile()
    {
        var settings = new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
            DefaultSlot = 2,
        };

        var decision = ForegroundRules.Evaluate(settings, Program("firefox"));

        Assert.Equal(ForegroundDecisionKind.Default, decision.Kind);
        Assert.Equal(2, decision.Slot);
        Assert.Null(decision.Rule);
    }

    [Fact]
    public void The_Desktop_Itself_Uses_The_Default_Profile()
    {
        var settings = new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
            DefaultSlot = 2,
        };

        var decision = ForegroundRules.Evaluate(settings, ForegroundContext.Empty);

        Assert.Equal(ForegroundDecisionKind.Default, decision.Kind);
        Assert.Equal(2, decision.Slot);
    }

    [Fact]
    public void A_Native_Gamepad_Rule_Pauses_Mapping_Instead_Of_Switching_Profile()
    {
        var settings = new ForegroundSettings
        {
            Rules = [Rule("game", priority: 3, action: ForegroundAction.NativeGamepad)],
            DefaultSlot = 1,
        };

        var decision = ForegroundRules.Evaluate(settings, Program("game"));

        Assert.Equal(ForegroundDecisionKind.Paused, decision.Kind);
        // A pause leaves the profile alone: it switches nothing, so the decision names the
        // desktop default rather than a slot the rule asked for.
        Assert.Equal(1, decision.Slot);
        Assert.NotNull(decision.Rule);
    }

    [Fact]
    public void A_Native_Gamepad_Rule_Beats_A_Profile_Rule_Only_By_Priority()
    {
        var settings = new ForegroundSettings
        {
            Rules =
            [
                Rule("game", priority: 1, action: ForegroundAction.Profile, slot: 4),
                Rule("game", priority: 2, action: ForegroundAction.NativeGamepad),
            ],
        };

        Assert.Equal(ForegroundDecisionKind.Paused, ForegroundRules.Evaluate(settings, Program("game")).Kind);

        // Reversing the priorities reverses the outcome: the pause is not special-cased.
        var reversed = new ForegroundSettings
        {
            Rules =
            [
                Rule("game", priority: 9, action: ForegroundAction.Profile, slot: 4),
                Rule("game", priority: 2, action: ForegroundAction.NativeGamepad),
            ],
        };

        Assert.Equal(ForegroundDecisionKind.Profile, ForegroundRules.Evaluate(reversed, Program("game")).Kind);
    }

    [Fact]
    public void Every_Decision_Explains_Why_The_Active_Profile_Is_The_Active_One()
    {
        var matched = new ForegroundSettings
        {
            Rules = [Rule("chrome.exe", priority: 6, slot: 3)],
            DefaultSlot = 2,
        };
        var paused = new ForegroundSettings
        {
            Rules = [Rule("game", priority: 6, action: ForegroundAction.NativeGamepad)],
        };

        var hit = ForegroundRules.Evaluate(matched, Program("Chrome"));
        var miss = ForegroundRules.Evaluate(matched, Program("firefox"));
        var desktop = ForegroundRules.Evaluate(matched, ForegroundContext.Empty);
        var native = ForegroundRules.Evaluate(paused, Program("game"));

        Assert.Contains("chrome.exe", hit.Reason);
        Assert.Contains("预设 3", hit.Reason);
        Assert.Contains("firefox", miss.Reason);
        Assert.Contains("预设 2", miss.Reason);
        Assert.Contains("桌面", desktop.Reason);
        Assert.Contains("原生手柄", native.Reason);
    }

    [Fact]
    public void Disabled_Rules_Leave_The_Desktop_Default_In_Charge()
    {
        var settings = new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 3)],
            DefaultSlot = 2,
            Enabled = false,
        };

        var decision = ForegroundRules.Evaluate(settings, Program("chrome"));

        Assert.Equal(ForegroundDecisionKind.Default, decision.Kind);
        Assert.Equal(2, decision.Slot);
        Assert.Contains("停用", decision.Reason);
    }

    [Fact]
    public void A_Rule_Describes_Itself_For_The_Rule_List()
    {
        Assert.Equal("chrome → 预设 3", Rule("chrome", slot: 3).Describe());
        Assert.Equal("game → 原生手柄", Rule("game", action: ForegroundAction.NativeGamepad).Describe());
        Assert.Equal("game → 预设 2（仅全屏）", Rule("game", slot: 2, fullscreenOnly: true).Describe());
        Assert.Equal("* → 原生手柄", Rule("*", action: ForegroundAction.NativeGamepad).Describe());
    }

    [Fact]
    public void A_Hand_Edited_Slot_Below_One_Is_Clamped_Rather_Than_Refused()
    {
        // The table is hand-editable JSON, and the host's switch path refuses a slot below 1
        // outright: clamping here costs one bad rule instead of the whole poll.
        var rules = new ForegroundSettings
        {
            Rules = [Rule("chrome", slot: 0)],
            DefaultSlot = -3,
        };

        Assert.Equal(1, ForegroundRules.Evaluate(rules, Program("chrome")).Slot);
        Assert.Equal(1, ForegroundRules.Evaluate(rules, Program("firefox")).Slot);
        Assert.Contains("预设 1", ForegroundRules.Evaluate(rules, Program("firefox")).Reason);
    }
}
