namespace DeziroJoy.Platform.Windows.Gaming;

/// <summary>
/// The vocabulary of the <c>ms-gamebar</c> protocol workaround, shared by the registration
/// (<see cref="GameBarPopupGuard"/>) and the silent command-line invocation the registration
/// performs. The shell shows its "choose an app for this link" dialog when nothing handles
/// <c>ms-gamebar:</c>, which is what a missing or broken Xbox Game Bar leaves behind; a
/// per-user no-op handler answers the link instead of the dialog.
/// </summary>
public static class GameBarProtocol
{
    /// <summary>The protocol whose missing handler produces the "open ms-gamebar" dialog.</summary>
    public const string Name = "ms-gamebar";

    /// <summary>
    /// The command-line argument a registered invocation carries. It is matched
    /// case-insensitively and only ever followed by exactly one <c>ms-gamebar:</c> URI.
    /// </summary>
    public const string NoopArgument = "--noop-gamebar";

    /// <summary>
    /// Builds the per-user registration command for <c>shell\open\command</c>. Both the
    /// executable and the substituted <c>%1</c> are quoted, so a URI can never be parsed
    /// into extra arguments, and an executable path that itself contains a quote is
    /// refused rather than silently producing an injectable command line.
    /// </summary>
    public static string BuildCommand(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new ArgumentException("可执行文件路径不能为空。", nameof(executable));
        }

        if (executable.Contains('"'))
        {
            throw new ArgumentException("可执行文件路径包含引号，无法安全注册。", nameof(executable));
        }

        return $"\"{executable}\" {NoopArgument} \"%1\"";
    }

    /// <summary>
    /// The executable a registration must name, or null when this process is the dotnet
    /// host - under "dotnet run" the muxer would be registered and the handler would
    /// never start the app.
    /// </summary>
    public static string? ResolveExecutable()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)
            || string.Equals(
                Path.GetFileNameWithoutExtension(executable),
                "dotnet",
                StringComparison.OrdinalIgnoreCase)
            || executable.Contains('"'))
        {
            return null;
        }

        return executable;
    }

    /// <summary>
    /// Recognizes the silent invocation a registered handler performs and reports it as
    /// handled with exit code 0. The URI is never interpreted, opened or passed on - the
    /// only check is that it really is an <c>ms-gamebar:</c> link, so a command line that
    /// merely borrows the argument name cannot drag another flag along with it. Anything
    /// else - a missing or foreign argument - exits 2, and a manual misuse prints why.
    /// </summary>
    public static bool TryRunNoop(IReadOnlyList<string> args, out int exitCode)
    {
        exitCode = 0;

        var index = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], NoopArgument, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return false;
        }

        if (args.Count != index + 2)
        {
            Console.Error.WriteLine($"用法：DeziroJoy {NoopArgument} {Name}:...");
            exitCode = 2;
            return true;
        }

        var target = args[index + 1];
        if (!target.StartsWith(Name + ":", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"拒绝处理非 {Name}: 协议的参数。");
            exitCode = 2;
            return true;
        }

        exitCode = 0;
        return true;
    }
}
