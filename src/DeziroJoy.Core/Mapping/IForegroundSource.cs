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
/// <para>
/// The same platform knowledge answers a second question for the rules panel: which windows are
/// open right now, so a rule can name its program by picking one instead of typing it. That is
/// <see cref="ListWindows"/>, and it fails the same way - a source that cannot enumerate returns
/// an empty list rather than guessing, exactly as <see cref="NullForegroundSource"/> does.
/// </para>
/// </summary>
public interface IForegroundSource
{
    ForegroundContext Read();

    /// <summary>
    /// The windows that are open right now, each with the process behind it and the title it
    /// carries for display. Empty where this platform cannot enumerate them - Wayland, a
    /// platform with no window query at all - which leaves the picker with nothing to offer and
    /// the user typing the process name, the same fallback the rules always had.
    /// </summary>
    IReadOnlyList<ForegroundWindow> ListWindows() => [];
}

/// <summary>
/// A source that never sees a program. The default on a platform with no foreground query, and
/// the stand-in for tests that only care about the rule table. It enumerates no windows either:
/// a source with nothing to read has nothing to offer the picker, and the rules panel says so
/// rather than showing an empty drop-down with no explanation.
/// </summary>
public sealed class NullForegroundSource : IForegroundSource
{
    public static NullForegroundSource Instance { get; } = new();

    public ForegroundContext Read() => ForegroundContext.Empty;

    /// <summary>
    /// No windows, and said outright rather than inherited: nothing can be read, so nothing can
    /// be enumerated. Left to the interface default this would be an accident of the default
    /// implementation rather than the answer this source means to give.
    /// </summary>
    public IReadOnlyList<ForegroundWindow> ListWindows() => [];
}
