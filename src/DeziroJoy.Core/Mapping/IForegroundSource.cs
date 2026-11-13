namespace DeziroJoy.Core.Mapping;

/// <summary>
/// Reads the program in front. Implemented per platform - GetForegroundWindow plus the process
/// name on Windows, a best-effort X11 query on Linux - and injected everywhere else, which is
/// what lets the rule table be tested against a fake source with no window system involved.
/// <para>
/// A source that cannot answer returns <see cref="ForegroundContext.Empty"/>: no rule matches and
/// the desktop default profile runs. That is the honest answer under Wayland, where no global
/// foreground query exists at all, and it is why no rule may depend on fullscreen being
/// detectable.
/// </para>
/// </summary>
public interface IForegroundSource
{
    ForegroundContext Read();
}

/// <summary>
/// A source that never sees a program. The default on a platform with no foreground query, and
/// the stand-in for tests that only care about the rule table.
/// </summary>
public sealed class NullForegroundSource : IForegroundSource
{
    public static NullForegroundSource Instance { get; } = new();

    public ForegroundContext Read() => ForegroundContext.Empty;
}
