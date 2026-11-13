namespace DeziroJoy.Core.Mapping;

/// <summary>
/// What one foreground rule does once it matches the program in front.
/// </summary>
public enum ForegroundAction
{
    /// <summary>Activate the profile (preset slot) the rule names.</summary>
    Profile,

    /// <summary>
    /// Pause keyboard and mouse mapping so the program takes the pad's native input: a game
    /// that reads the controller itself then sees the pad exactly as it would with DeziroJoy
    /// absent, and the mapped keys never fire inside it.
    /// </summary>
    NativeGamepad,
}

/// <summary>
/// The foreground state a rule is matched against: the program in front and, where the platform
/// can tell, whether its window is fullscreen. <see cref="IsFullscreen"/> is null when the
/// platform cannot answer - Wayland has no global foreground-window query at all - which is a
/// different state from "windowed", and one no rule may be silently disabled by.
/// </summary>
public sealed record ForegroundContext(string? ProcessName, bool? IsFullscreen)
{
    /// <summary>No foreground program: the desktop, or a platform that cannot read one.</summary>
    public static ForegroundContext Empty { get; } = new(null, null);
}

/// <summary>
/// One window that is open right now, as the picker names it: the process behind it and the
/// title it carries. The title is what the user reads to tell two windows of the same program
/// apart; matching stays by process name alone, which is the one part every platform can
/// answer - so a window with no title still lists, labelled by its process.
/// </summary>
public sealed record ForegroundWindow(string ProcessName, string? Title)
{
    /// <summary>What the picker shows: the process, and its title where there is one.</summary>
    public string Display => string.IsNullOrWhiteSpace(Title) ? ProcessName : $"{ProcessName} — {Title}";
}

/// <summary>
/// One rule of the foreground table: while this program is in front, run that profile - or
/// pause mapping so the program gets the pad itself. Matching is by process name; several
/// rules may name the same program and <see cref="Priority"/> decides which one applies.
/// </summary>
public sealed record ForegroundRule
{
    /// <summary>The one rule shape that is not about a single process: it matches every program.</summary>
    public const string AnyProcess = "*";

    /// <summary>The slot a rule names when it does not say otherwise: the device's own profile.</summary>
    public const int DefaultSlot = 1;

    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// The program this rule names, as a process name with or without its .exe suffix. Matching
    /// ignores case, surrounding whitespace and the suffix, so "Chrome.exe" and "chrome" are the
    /// same program; <see cref="AnyProcess"/> matches every program.
    /// </summary>
    public required string ProcessName { get; init; }

    /// <summary>Higher wins. A tie keeps the first rule in list order, so the order is meaningful.</summary>
    public int Priority { get; init; }

    public ForegroundAction Action { get; init; } = ForegroundAction.Profile;

    /// <summary>The profile a <see cref="ForegroundAction.Profile"/> rule activates.</summary>
    public int Slot { get; init; } = DefaultSlot;

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Only match while the program's window is fullscreen. An auxiliary condition the user
    /// turns on per rule and off again - never a lock-in - and one that cannot silently disable
    /// a rule: on a platform that cannot detect fullscreen the rule still matches, because the
    /// question goes unanswered rather than answered "no".
    /// </summary>
    public bool FullscreenOnly { get; init; }

    /// <summary>Whether this rule applies to the foreground state in <paramref name="context"/>.</summary>
    public bool Matches(ForegroundContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Enabled || string.IsNullOrWhiteSpace(context.ProcessName))
        {
            return false;
        }

        var program = NormalizeProcessName(context.ProcessName);
        var named = NormalizeProcessName(ProcessName);

        if (named.Length == 0)
        {
            return false;
        }

        if (!string.Equals(named, AnyProcess, StringComparison.Ordinal)
            && !string.Equals(named, program, StringComparison.Ordinal))
        {
            return false;
        }

        return !FullscreenOnly || context.IsFullscreen != false;
    }

    /// <summary>
    /// A process name reduced to what matching compares: trimmed, without a .exe suffix and
    /// lower-cased. Both sides go through it, so a user who types "Chrome.exe" and a platform
    /// that reports "chrome" still meet.
    /// </summary>
    public static string NormalizeProcessName(string? name)
    {
        var trimmed = (name ?? string.Empty).AsSpan().Trim();

        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed.ToString().ToLowerInvariant();
    }

    /// <summary>The rule as the rule list and the reason line name it.</summary>
    public string Describe()
    {
        var action = Action == ForegroundAction.NativeGamepad ? "原生手柄" : $"预设 {Slot}";
        var scope = FullscreenOnly ? "（仅全屏）" : string.Empty;
        return $"{ProcessName} → {action}{scope}";
    }
}

/// <summary>What the foreground state resolves to: which profile runs, or none because mapping is paused.</summary>
public enum ForegroundDecisionKind
{
    /// <summary>No rule matched: the desktop default profile.</summary>
    Default,

    /// <summary>A rule named a profile.</summary>
    Profile,

    /// <summary>A rule asked for native gamepad input, so keyboard and mouse mapping is paused.</summary>
    Paused,
}

/// <summary>
/// The outcome of evaluating the rule table against one foreground state: what should run, the
/// rule that decided it (null for the desktop default), and the reason the UI quotes so the user
/// never has to guess why the active profile changed.
/// </summary>
public sealed record ForegroundDecision(ForegroundDecisionKind Kind, int Slot, ForegroundRule? Rule, string Reason)
{
    /// <summary>The desktop default: no rule matched, or the rules are switched off.</summary>
    public static ForegroundDecision Desktop(int slot, string reason)
        => new(ForegroundDecisionKind.Default, slot, null, reason);
}

/// <summary>
/// Rule matching and priority, in one place: one foreground state in, one decision out. Pure and
/// deterministic, which is what makes it testable against a fake foreground source with no
/// platform, no engine and no window involved.
/// </summary>
public static class ForegroundRules
{
    /// <summary>
    /// Resolves <paramref name="context"/> against the rule table. A matching rule wins by
    /// <see cref="ForegroundRule.Priority"/>; a miss - no rule, the desktop, or the whole table
    /// switched off - falls back to <see cref="ForegroundSettings.DefaultSlot"/>.
    /// </summary>
    public static ForegroundDecision Evaluate(ForegroundSettings settings, ForegroundContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);

        var defaultSlot = SafeSlot(settings.DefaultSlot);

        if (!settings.Enabled)
        {
            return ForegroundDecision.Desktop(
                defaultSlot,
                $"前台规则已停用：使用桌面默认配置（预设 {defaultSlot}）");
        }

        var match = Match(settings.Rules, context);

        if (match is null)
        {
            return ForegroundDecision.Desktop(
                defaultSlot,
                string.IsNullOrWhiteSpace(context.ProcessName)
                    ? $"当前不在任何程序中（桌面）：使用桌面默认配置（预设 {defaultSlot}）"
                    : $"没有规则匹配「{context.ProcessName}」：使用桌面默认配置（预设 {defaultSlot}）");
        }

        return match.Action == ForegroundAction.NativeGamepad
            ? new ForegroundDecision(
                ForegroundDecisionKind.Paused,
                defaultSlot,
                match,
                $"命中规则「{match.Describe()}」（优先级 {match.Priority}）："
                    + $"{context.ProcessName} 使用原生手柄输入，已暂停键鼠映射")
            : new ForegroundDecision(
                ForegroundDecisionKind.Profile,
                SafeSlot(match.Slot),
                match,
                $"命中规则「{match.Describe()}」（优先级 {match.Priority}）："
                    + $"前台为「{context.ProcessName}」，已切换到预设 {SafeSlot(match.Slot)}");
    }

    /// <summary>
    /// The slot a decision may name, clamped into the range the preset store accepts. The table
    /// is hand-editable JSON, so a slot below 1 is possible - and the host's own switch path
    /// refuses one outright, which would take the whole poll down rather than the one bad rule.
    /// </summary>
    private static int SafeSlot(int slot) => Math.Max(slot, ForegroundRule.DefaultSlot);

    /// <summary>
    /// The highest-priority rule that matches, or null when none does. The first rule in list
    /// order breaks a tie, so a table of equal priorities stays deterministic.
    /// </summary>
    private static ForegroundRule? Match(IReadOnlyList<ForegroundRule> rules, ForegroundContext context)
    {
        ForegroundRule? best = null;

        foreach (var rule in rules)
        {
            if (!rule.Matches(context))
            {
                continue;
            }

            if (best is null || rule.Priority > best.Priority)
            {
                best = rule;
            }
        }

        return best;
    }
}
