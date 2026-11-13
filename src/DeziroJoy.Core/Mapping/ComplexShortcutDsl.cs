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
/// Zero-dependency parser and formatter for the complex-shortcut DSL. One step per line;
/// blank lines and <c>#</c> comments are skipped. The script is two halves: up to three
/// header lines that say when the program starts and how it ends
/// (<c>trigger</c> / <c>run</c> / <c>repeat</c>), then the steps themselves
/// (<c>tap</c>, <c>hold</c>, <c>release</c>, <c>wait</c>, <c>every &lt;时长&gt; tap &lt;按键&gt;</c>,
/// <c>click &lt;鼠标按键&gt;</c>, <c>text &lt;内容&gt;</c>).
/// </summary>
/// <remarks>
/// <para>
/// Everything is bounded so a hand-edited script cannot grow without limit: at most
/// <see cref="MaxSteps"/> steps, and every duration within
/// [<see cref="MinDuration"/>, <see cref="MaxDuration"/>] seconds. The parser is pure
/// text work - no eval, no reflection over user input, no shell - and produces only
/// standard <see cref="ComplexShortcutStep"/>s.
/// </para>
/// <para>
/// The headers are optional and must precede the first step; a script with none is a
/// long-press program that runs while the button is held, which is what every profile
/// written before the headers existed says. <see cref="Format"/> writes them back out only
/// when they differ from that default, so an old script round-trips byte for byte.
/// </para>
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

    private static readonly Dictionary<string, MouseButton> ButtonNames = BuildButtonNames();

    private static readonly Dictionary<string, ActionTrigger> TriggerNames = BuildTriggerNames();

    private static readonly Dictionary<string, ActionExecution> ExecutionNames = BuildExecutionNames();

    private static readonly Dictionary<string, ActionRepeat> RepeatNames = BuildRepeatNames();

    /// <summary>Parses a script into its closed step list, ignoring the header lines.</summary>
    /// <exception cref="ComplexShortcutParseException">The script is empty or malformed; the message is Chinese and carries line and column.</exception>
    public static IReadOnlyList<ComplexShortcutStep> Parse(string script)
    {
        var (_, steps) = ParseScript(script);
        return steps;
    }

    /// <summary>
    /// Parses a script into the whole target it describes: the three header fields plus the
    /// step list they run. This is the one entry point the editor and the engine share, so
    /// a program configured in the UI and one typed as text are the same record.
    /// </summary>
    /// <exception cref="ComplexShortcutParseException">The script is empty or malformed.</exception>
    public static ComplexShortcutTarget ParseTarget(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var (headers, steps) = ParseScript(script);
        return new ComplexShortcutTarget
        {
            Script = script,
            Trigger = headers.Trigger,
            Execution = headers.Execution,
            Repeat = headers.Repeat,
        };
    }

    /// <summary>
    /// The script one target describes, in canonical form: the headers it needs and nothing
    /// else, then its steps in order. Parsing what this writes gives back the same program,
    /// which is what lets the editor round-trip a structured draft through the text box the
    /// DSL shares.
    /// </summary>
    public static string Format(ComplexShortcutTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var lines = new List<string>();

        if (target.Trigger != ComplexShortcutTarget.DefaultTrigger)
        {
            lines.Add($"trigger {TriggerWord(target.Trigger)}");
        }

        if (target.Execution != ComplexShortcutTarget.DefaultExecution)
        {
            lines.Add($"run {ExecutionWord(target.Execution)}");
        }

        if (target.Repeat != ComplexShortcutTarget.DefaultRepeat)
        {
            lines.Add($"repeat {RepeatWord(target.Repeat)}");
        }

        lines.AddRange(FormatSteps(ComplexShortcutDsl.Parse(target.Script)));

        return string.Join('\n', lines);
    }

    /// <summary>The step lines of a compiled step list, one line per step.</summary>
    public static IReadOnlyList<string> FormatSteps(IReadOnlyList<ComplexShortcutStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var lines = new List<string>(steps.Count);
        foreach (var step in steps)
        {
            lines.Add(step switch
            {
                ComplexShortcutTapStep tap => $"tap {tap.Key}",
                ComplexShortcutHoldStep hold => $"hold {hold.Key}",
                ComplexShortcutReleaseStep release => $"release {release.Key}",
                ComplexShortcutWaitStep wait => $"wait {Duration(wait.Seconds)}",
                ComplexShortcutEveryStep every => $"every {Duration(every.Interval)} tap {every.Key}",
                ComplexShortcutClickStep click => $"click {click.Button}",
                ComplexShortcutTextStep text => $"text {text.Text}",
                _ => throw new ArgumentException(
                    $"A complex shortcut step of type {step.GetType().Name} has no DSL form.", nameof(steps)),
            });
        }

        return lines;
    }

    /// <summary>A duration as the DSL spells it: 1s, 0.5s, 500ms trimmed to 0.5s.</summary>
    public static string Duration(double seconds)
        => $"{seconds.ToString("0.###", CultureInfo.InvariantCulture)}s";

    private static (ScriptHeaders Headers, List<ComplexShortcutStep> Steps) ParseScript(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var headers = new ScriptHeaders();
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

            if (TryReadHeader(tokens, line, lineNumber + 1, ref headers))
            {
                // A header after a step would silently re-describe a program already
                // underway, so it is refused rather than applied to the whole script.
                if (steps.Count > 0)
                {
                    throw new ComplexShortcutParseException(
                        $"「{tokens[0].Text}」只能写在脚本开头：触发方式、执行方式与重复方式要在第一步之前设定",
                        lineNumber + 1,
                        tokens[0].Column);
                }

                continue;
            }

            if (steps.Count >= MaxSteps)
            {
                throw new ComplexShortcutParseException(
                    $"步骤过多：一个复杂快捷键最多 {MaxSteps} 步", lineNumber + 1, tokens[0].Column);
            }

            steps.Add(ParseStep(tokens, line, lineNumber + 1));
        }

        if (steps.Count == 0)
        {
            throw new ComplexShortcutParseException(
                "脚本为空：至少需要一个步骤（tap / hold / release / wait / every / click / text）", 1, 1);
        }

        return (headers, steps);
    }

    /// <summary>The three header fields a script carries, at the defaults until a line sets them.</summary>
    private struct ScriptHeaders
    {
        public ScriptHeaders()
        {
        }

        public ActionTrigger Trigger { get; set; } = ComplexShortcutTarget.DefaultTrigger;

        public ActionExecution Execution { get; set; } = ComplexShortcutTarget.DefaultExecution;

        public ActionRepeat Repeat { get; set; } = ComplexShortcutTarget.DefaultRepeat;
    }

    private static bool TryReadHeader(List<Token> tokens, string line, int lineNumber, ref ScriptHeaders headers)
    {
        var command = tokens[0].Text.ToLowerInvariant();

        switch (command)
        {
            case "trigger":
            case "触发":
                RequireArguments(tokens, 2, "trigger <长按|单击|双击|松开>", lineNumber);
                headers.Trigger = Resolve(TriggerNames, tokens[1], lineNumber, "触发方式",
                    "trigger <长按|单击|双击|松开>");
                return true;

            case "run":
            case "执行":
                RequireArguments(tokens, 2, "run <按住期间|一次|切换>", lineNumber);
                headers.Execution = Resolve(ExecutionNames, tokens[1], lineNumber, "执行方式",
                    "run <按住期间|一次|切换>");
                return true;

            case "repeat":
            case "重复":
                RequireArguments(tokens, 2, "repeat <重新开始|忽略|排队>", lineNumber);
                headers.Repeat = Resolve(RepeatNames, tokens[1], lineNumber, "重复方式",
                    "repeat <重新开始|忽略|排队>");
                return true;

            default:
                return false;
        }
    }

    private static ComplexShortcutStep ParseStep(List<Token> tokens, string line, int lineNumber)
    {
        var command = tokens[0];

        switch (command.Text.ToLowerInvariant())
        {
            case "tap":
                RequireArguments(tokens, 2, "tap <按键>", lineNumber);
                return new ComplexShortcutTapStep(ResolveKey(tokens[1], lineNumber));

            case "hold":
                RequireArguments(tokens, 2, "hold <按键>", lineNumber);
                return new ComplexShortcutHoldStep(ResolveKey(tokens[1], lineNumber));

            case "release":
                RequireArguments(tokens, 2, "release <按键>", lineNumber);
                return new ComplexShortcutReleaseStep(ResolveKey(tokens[1], lineNumber));

            case "wait":
                RequireArguments(tokens, 2, "wait <时长>", lineNumber);
                return new ComplexShortcutWaitStep(ResolveDuration(tokens[1], lineNumber));

            case "every":
                RequireArguments(tokens, 4, "every <时长> tap <按键>", lineNumber);
                if (!tokens[2].Text.Equals("tap", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ComplexShortcutParseException(
                        $"every 之后应为 tap，得到 \"{tokens[2].Text}\"：应为 \"every <时长> tap <按键>\" 形式",
                        lineNumber,
                        tokens[2].Column);
                }

                var interval = ResolveDuration(tokens[1], lineNumber);
                return new ComplexShortcutEveryStep(interval, ResolveKey(tokens[3], lineNumber));

            case "click":
                RequireArguments(tokens, 2, "click <鼠标按键>", lineNumber);
                return new ComplexShortcutClickStep(ResolveButton(tokens[1], lineNumber));

            case "text":
                // The text is the rest of the line, spaces and all: a search query or a URL
                // is the whole point of the step, and splitting it into tokens would lose
                // the spaces that make it one string.
                return new ComplexShortcutTextStep(RestOfLine(tokens[0], line, lineNumber));

            default:
                throw new ComplexShortcutParseException(
                    $"未知指令 \"{command.Text}\"：一行应以 tap / hold / release / wait / every / click / text "
                    + "之一开始，或以 trigger / run / repeat 之一设定触发方式",
                    lineNumber,
                    command.Column);
        }
    }

    /// <summary>Everything after the keyword token, with the leading spaces between them removed.</summary>
    private static string RestOfLine(Token keyword, string line, int lineNumber)
    {
        var start = keyword.Column - 1 + keyword.Text.Length;
        var text = start <= line.Length ? line[start..].TrimStart() : string.Empty;

        if (text.Length == 0)
        {
            throw new ComplexShortcutParseException(
                "text 之后缺少内容：应为 \"text <内容>\" 形式", lineNumber, keyword.Column);
        }

        return text;
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

    private static T Resolve<T>(
        Dictionary<string, T> names,
        Token token,
        int line,
        string what,
        string form)
        where T : struct
    {
        if (!names.TryGetValue(token.Text, out var value))
        {
            throw new ComplexShortcutParseException(
                $"未知{what} \"{token.Text}\"：应为 \"{form}\" 形式", line, token.Column);
        }

        return value;
    }

    private static GamepadKey ResolveKey(Token token, int line)
    {
        if (!KeyNames.TryGetValue(token.Text, out var key))
        {
            throw new ComplexShortcutParseException($"未知按键 \"{token.Text}\"", line, token.Column);
        }

        return key;
    }

    private static MouseButton ResolveButton(Token token, int line)
    {
        if (!ButtonNames.TryGetValue(token.Text, out var button))
        {
            throw new ComplexShortcutParseException(
                $"未知鼠标按键 \"{token.Text}\"：应为 left / right / middle / x1 / x2 之一", line, token.Column);
        }

        return button;
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

        // The media and system keys: their enum names are already canonical, and these are
        // the words a hand-written script actually types.
        names["volumeup"] = GamepadKey.VolumeUp;
        names["volup"] = GamepadKey.VolumeUp;
        names["volumedown"] = GamepadKey.VolumeDown;
        names["voldown"] = GamepadKey.VolumeDown;
        names["volumemute"] = GamepadKey.VolumeMute;
        names["mute"] = GamepadKey.VolumeMute;
        names["playpause"] = GamepadKey.MediaPlayPause;
        names["play"] = GamepadKey.MediaPlayPause;
        names["mediastop"] = GamepadKey.MediaStop;
        names["prevtrack"] = GamepadKey.MediaPrevTrack;
        names["previous"] = GamepadKey.MediaPrevTrack;
        names["nexttrack"] = GamepadKey.MediaNextTrack;
        names["next"] = GamepadKey.MediaNextTrack;
        names["browserback"] = GamepadKey.BrowserBack;
        names["browserforward"] = GamepadKey.BrowserForward;
        names["refresh"] = GamepadKey.BrowserRefresh;
        names["browserhome"] = GamepadKey.BrowserHome;
        names["browsersearch"] = GamepadKey.BrowserSearch;
        names["favorites"] = GamepadKey.BrowserFavorites;
        names["mail"] = GamepadKey.LaunchMail;
        names["sleep"] = GamepadKey.Sleep;
        names["printscreen"] = GamepadKey.PrintScreen;

        return names;
    }

    private static Dictionary<string, MouseButton> BuildButtonNames()
    {
        var names = new Dictionary<string, MouseButton>(StringComparer.OrdinalIgnoreCase);

        foreach (var button in Enum.GetValues<MouseButton>())
        {
            names[button.ToString()] = button;
        }

        names["l"] = MouseButton.Left;
        names["r"] = MouseButton.Right;
        names["m"] = MouseButton.Middle;
        names["middle"] = MouseButton.Middle;
        names["x1"] = MouseButton.XButton1;
        names["x2"] = MouseButton.XButton2;
        names["side"] = MouseButton.XButton1;
        names["extra"] = MouseButton.XButton2;

        return names;
    }

    private static Dictionary<string, ActionTrigger> BuildTriggerNames()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["longpress"] = ActionTrigger.LongPress,
            ["long"] = ActionTrigger.LongPress,
            ["press"] = ActionTrigger.Press,
            ["click"] = ActionTrigger.Press,
            ["doubleclick"] = ActionTrigger.DoubleClick,
            ["double"] = ActionTrigger.DoubleClick,
            ["release"] = ActionTrigger.Release,
            ["长按"] = ActionTrigger.LongPress,
            ["单击"] = ActionTrigger.Press,
            ["按下"] = ActionTrigger.Press,
            ["双击"] = ActionTrigger.DoubleClick,
            ["松开"] = ActionTrigger.Release,
        };

    private static Dictionary<string, ActionExecution> BuildExecutionNames()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["whileheld"] = ActionExecution.WhileHeld,
            ["held"] = ActionExecution.WhileHeld,
            ["once"] = ActionExecution.Once,
            ["toggle"] = ActionExecution.Toggle,
            ["按住期间"] = ActionExecution.WhileHeld,
            ["按住"] = ActionExecution.WhileHeld,
            ["一次"] = ActionExecution.Once,
            ["切换"] = ActionExecution.Toggle,
            ["再按切换"] = ActionExecution.Toggle,
        };

    private static Dictionary<string, ActionRepeat> BuildRepeatNames()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["restart"] = ActionRepeat.Restart,
            ["ignore"] = ActionRepeat.Ignore,
            ["queue"] = ActionRepeat.Queue,
            ["重新开始"] = ActionRepeat.Restart,
            ["重来"] = ActionRepeat.Restart,
            ["忽略"] = ActionRepeat.Ignore,
            ["排队"] = ActionRepeat.Queue,
        };

    private static string TriggerWord(ActionTrigger trigger) => trigger switch
    {
        ActionTrigger.Press => "press",
        ActionTrigger.DoubleClick => "doubleclick",
        ActionTrigger.Release => "release",
        _ => "longpress",
    };

    private static string ExecutionWord(ActionExecution execution) => execution switch
    {
        ActionExecution.Once => "once",
        ActionExecution.Toggle => "toggle",
        _ => "whileheld",
    };

    private static string RepeatWord(ActionRepeat repeat) => repeat switch
    {
        ActionRepeat.Ignore => "ignore",
        ActionRepeat.Queue => "queue",
        _ => "restart",
    };

    private readonly record struct Token(string Text, int Column);
}
