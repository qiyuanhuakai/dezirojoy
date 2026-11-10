using System.Globalization;

namespace DeziroJoy.Core.Mapping;

/// <summary>
/// A hand-written complex-shortcut script failed to parse. The message is Chinese and
/// names the line and column, because the script is hand-edited inside a profile and
/// the author needs to find the offending token without counting characters.
/// </summary>
public sealed class ComplexShortcutParseException : Exception
{
    public ComplexShortcutParseException(string message, int line, int column)
        : base($"第 {line} 行第 {column} 列：{message}")
    {
        Line = line;
        Column = column;
    }

    public ComplexShortcutParseException(string message)
        : base(message)
    {
    }

    public ComplexShortcutParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>1-based line the error points at; 0 when the exception was built without a position.</summary>
    public int Line { get; }

    /// <summary>1-based column within <see cref="Line"/>; 0 when the exception was built without a position.</summary>
    public int Column { get; }
}

/// <summary>
/// Zero-dependency parser for the complex-shortcut DSL. One step per line; blank lines
/// and <c>#</c> comments are skipped. Commands: <c>tap</c>, <c>hold</c>, <c>release</c>,
/// <c>wait</c> and <c>every &lt;时长&gt; tap &lt;按键&gt;</c>. Keys are the fixed
/// <see cref="GamepadKey"/> vocabulary plus friendly aliases (alt, ctrl, esc, up, ...);
/// durations are positive numbers with an optional <c>s</c> or <c>ms</c> suffix.
/// </summary>
/// <remarks>
/// Everything is bounded so a hand-edited script cannot grow without limit: at most
/// <see cref="MaxSteps"/> steps, and every duration within
/// [<see cref="MinDuration"/>, <see cref="MaxDuration"/>] seconds. The parser is pure
/// text work - no eval, no reflection over user input, no shell - and produces only
/// standard <see cref="ComplexShortcutStep"/>s.
/// </remarks>
public static class ComplexShortcutDsl
{
    /// <summary>Upper bound on the steps one program may hold.</summary>
    public const int MaxSteps = 64;

    /// <summary>Lower bound for every duration, in seconds: a faster wait or tap interval is abusive, not useful.</summary>
    public const double MinDuration = 0.05;

    /// <summary>Upper bound for every duration, in seconds.</summary>
    public const double MaxDuration = 60.0;

    private static readonly Dictionary<string, GamepadKey> KeyNames = BuildKeyNames();

    /// <summary>Parses a script into its closed step list.</summary>
    /// <exception cref="ComplexShortcutParseException">The script is empty or malformed; the message is Chinese and carries line and column.</exception>
    public static IReadOnlyList<ComplexShortcutStep> Parse(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var steps = new List<ComplexShortcutStep>();
        var lines = script.Split('\n');

        for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
        {
            var line = lines[lineNumber];
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line[..comment];
            }

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                continue;
            }

            if (steps.Count >= MaxSteps)
            {
                throw new ComplexShortcutParseException(
                    $"步骤过多：一个复杂快捷键最多 {MaxSteps} 步", lineNumber + 1, tokens[0].Column);
            }

            steps.Add(ParseStep(tokens, lineNumber + 1));
        }

        if (steps.Count == 0)
        {
            throw new ComplexShortcutParseException(
                "脚本为空：至少需要一个步骤（tap / hold / release / wait / every）", 1, 1);
        }

        return steps;
    }

    private static ComplexShortcutStep ParseStep(List<Token> tokens, int line)
    {
        var command = tokens[0];

        switch (command.Text.ToLowerInvariant())
        {
            case "tap":
                RequireArguments(tokens, 2, "tap <按键>", line);
                return new ComplexShortcutTapStep(ResolveKey(tokens[1], line));

            case "hold":
                RequireArguments(tokens, 2, "hold <按键>", line);
                return new ComplexShortcutHoldStep(ResolveKey(tokens[1], line));

            case "release":
                RequireArguments(tokens, 2, "release <按键>", line);
                return new ComplexShortcutReleaseStep(ResolveKey(tokens[1], line));

            case "wait":
                RequireArguments(tokens, 2, "wait <时长>", line);
                return new ComplexShortcutWaitStep(ResolveDuration(tokens[1], line));

            case "every":
                RequireArguments(tokens, 4, "every <时长> tap <按键>", line);
                if (!tokens[2].Text.Equals("tap", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ComplexShortcutParseException(
                        $"every 之后应为 tap，得到 \"{tokens[2].Text}\"：应为 \"every <时长> tap <按键>\" 形式",
                        line,
                        tokens[2].Column);
                }

                var interval = ResolveDuration(tokens[1], line);
                return new ComplexShortcutEveryStep(interval, ResolveKey(tokens[3], line));

            default:
                throw new ComplexShortcutParseException(
                    $"未知指令 \"{command.Text}\"：一行应以 tap / hold / release / wait / every 之一开始",
                    line,
                    command.Column);
        }
    }

    private static void RequireArguments(List<Token> tokens, int count, string form, int line)
    {
        if (tokens.Count < count)
        {
            throw new ComplexShortcutParseException(
                $"指令 \"{tokens[0].Text}\" 缺少参数：应为 \"{form}\" 形式", line, tokens[0].Column);
        }

        if (tokens.Count > count)
        {
            throw new ComplexShortcutParseException(
                $"指令 \"{tokens[0].Text}\" 参数过多：应为 \"{form}\" 形式", line, tokens[0].Column);
        }
    }

    private static GamepadKey ResolveKey(Token token, int line)
    {
        if (!KeyNames.TryGetValue(token.Text, out var key))
        {
            throw new ComplexShortcutParseException($"未知按键 \"{token.Text}\"", line, token.Column);
        }

        return key;
    }

    private static double ResolveDuration(Token token, int line)
    {
        var text = token.Text;
        var milliseconds = text.EndsWith("ms", StringComparison.OrdinalIgnoreCase);

        if (milliseconds)
        {
            text = text[..^2];
        }
        else if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^1];
        }

        if (text.Length == 0
            || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || value <= 0d)
        {
            throw new ComplexShortcutParseException(
                $"无效时长 \"{token.Text}\"：应为正数，可带 s 或 ms 后缀（例如 1s、500ms）", line, token.Column);
        }

        // Divide rather than multiply by 0.001: IEEE division is correctly rounded, so
        // "500ms" lands on exactly the same double as the literal 0.5 and round-trips.
        var seconds = milliseconds ? value / 1000.0 : value;
        if (seconds < MinDuration || seconds > MaxDuration)
        {
            throw new ComplexShortcutParseException(
                $"时长 {seconds.ToString("0.###", CultureInfo.InvariantCulture)} 秒超出范围：" +
                $"必须在 {MinDuration.ToString("0.###", CultureInfo.InvariantCulture)} 秒到 " +
                $"{MaxDuration.ToString("0.###", CultureInfo.InvariantCulture)} 秒之间",
                line,
                token.Column);
        }

        return seconds;
    }

    private static List<Token> Tokenize(string line)
    {
        var tokens = new List<Token>();
        var index = 0;

        while (index < line.Length)
        {
            if (char.IsWhiteSpace(line[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < line.Length && !char.IsWhiteSpace(line[index]))
            {
                index++;
            }

            tokens.Add(new Token(line[start..index], start + 1));
        }

        return tokens;
    }

    private static Dictionary<string, GamepadKey> BuildKeyNames()
    {
        // The enum names themselves are the canonical spellings ("Tab", "F13", "D0");
        // the aliases below are what a hand-written script actually reaches for.
        var names = new Dictionary<string, GamepadKey>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in Enum.GetValues<GamepadKey>())
        {
            names[key.ToString()] = key;
        }

        // Bare digits are the digit-row keys: "0" is D0, "9" is D9.
        for (var digit = 0; digit <= 9; digit++)
        {
            names[digit.ToString(CultureInfo.InvariantCulture)] = GamepadKey.D0 + digit;
        }

        names["esc"] = GamepadKey.Escape;
        names["return"] = GamepadKey.Enter;
        names["bsp"] = GamepadKey.Backspace;
        names["del"] = GamepadKey.Delete;
        names["ins"] = GamepadKey.Insert;
        names["pgup"] = GamepadKey.PageUp;
        names["pageup"] = GamepadKey.PageUp;
        names["pgdn"] = GamepadKey.PageDown;
        names["pagedn"] = GamepadKey.PageDown;
        names["pagedown"] = GamepadKey.PageDown;
        names["up"] = GamepadKey.ArrowUp;
        names["arrowup"] = GamepadKey.ArrowUp;
        names["down"] = GamepadKey.ArrowDown;
        names["arrowdown"] = GamepadKey.ArrowDown;
        names["left"] = GamepadKey.ArrowLeft;
        names["arrowleft"] = GamepadKey.ArrowLeft;
        names["right"] = GamepadKey.ArrowRight;
        names["arrowright"] = GamepadKey.ArrowRight;
        names["ctrl"] = GamepadKey.LeftCtrl;
        names["lctrl"] = GamepadKey.LeftCtrl;
        names["shift"] = GamepadKey.LeftShift;
        names["lshift"] = GamepadKey.LeftShift;
        names["alt"] = GamepadKey.LeftAlt;
        names["lalt"] = GamepadKey.LeftAlt;
        names["win"] = GamepadKey.LeftWin;
        names["lwin"] = GamepadKey.LeftWin;
        names["windows"] = GamepadKey.LeftWin;
        names["minus"] = GamepadKey.Minus;
        names["-"] = GamepadKey.Minus;
        names["equal"] = GamepadKey.Equal;
        names["equals"] = GamepadKey.Equal;
        names["="] = GamepadKey.Equal;
        names["comma"] = GamepadKey.Comma;
        names[","] = GamepadKey.Comma;
        names["period"] = GamepadKey.Period;
        names["dot"] = GamepadKey.Period;
        names["."] = GamepadKey.Period;
        names["slash"] = GamepadKey.Slash;
        names["/"] = GamepadKey.Slash;
        names["semicolon"] = GamepadKey.Semicolon;
        names[";"] = GamepadKey.Semicolon;
        names["apostrophe"] = GamepadKey.Apostrophe;
        names["quote"] = GamepadKey.Apostrophe;
        names["'"] = GamepadKey.Apostrophe;
        names["["] = GamepadKey.BracketLeft;
        names["lbracket"] = GamepadKey.BracketLeft;
        names["]"] = GamepadKey.BracketRight;
        names["rbracket"] = GamepadKey.BracketRight;
        names["\\"] = GamepadKey.Backslash;
        names["`"] = GamepadKey.Grave;
        names["backtick"] = GamepadKey.Grave;

        return names;
    }

    private readonly record struct Token(string Text, int Column);
}
