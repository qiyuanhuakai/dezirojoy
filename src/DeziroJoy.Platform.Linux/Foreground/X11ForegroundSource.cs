using System.Runtime.InteropServices;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Platform.Linux.Foreground;

/// <summary>
/// The foreground program on Linux, best-effort, through X11.
/// <para>
/// What it can answer: under an X11 session with an EWMH window manager, the active toplevel
/// window comes off the root window's <c>_NET_ACTIVE_WINDOW</c>, its process off
/// <c>_NET_WM_PID</c> plus <c>/proc</c>, and its fullscreen state off <c>_NET_WM_STATE</c>.
/// </para>
/// <para>
/// What it cannot: under Wayland there is no global foreground-window query at all - the
/// protocol deliberately keeps one client from asking what another client has focused. An
/// XWayland client shows up, a native Wayland client does not, and a session with no X server
/// at all answers nothing. In every one of those cases this source returns
/// <see cref="ForegroundContext.Empty"/>, no rule matches, and the desktop default profile runs.
/// That is the documented reason no rule may depend on fullscreen being detectable: the
/// condition is auxiliary and the user turns it off per rule where the platform cannot answer.
/// </para>
/// </summary>
public sealed class X11ForegroundSource : IForegroundSource, IDisposable
{
    private const string ActiveWindowProperty = "_NET_ACTIVE_WINDOW";

    private const string ProcessIdProperty = "_NET_WM_PID";

    private const string WindowStateProperty = "_NET_WM_STATE";

    private const string FullscreenState = "_NET_WM_STATE_FULLSCREEN";

    private nint _connection;

    private bool _disposed;

    public ForegroundContext Read()
    {
        var connection = EnsureConnection();

        if (connection == 0)
        {
            return ForegroundContext.Empty;
        }

        var root = RootWindow(connection);
        if (root == 0)
        {
            return ForegroundContext.Empty;
        }

        var window = ActiveWindow(connection, root);
        if (window == 0)
        {
            return ForegroundContext.Empty;
        }

        var processName = ProcessName(ProcessId(connection, window));
        if (string.IsNullOrEmpty(processName))
        {
            return ForegroundContext.Empty;
        }

        return new ForegroundContext(processName, IsFullscreen(connection, window));
    }

    /// <summary>
    /// The connection, opened once and reused: the watcher is driven from one thread, so there
    /// is no concurrent use of one XCB connection to serialise. A connection that went bad - the
    /// X server went away - is dropped and reopened on the next read, because a dead connection
    /// would otherwise report "no foreground program" forever.
    /// </summary>
    private nint EnsureConnection()
    {
        if (_disposed)
        {
            return 0;
        }

        if (_connection != 0)
        {
            return XcbNative.ConnectionHasError(_connection) == 0 ? _connection : DropConnection();
        }

        _connection = XcbNative.Connect(0, 0);

        if (_connection == 0 || XcbNative.ConnectionHasError(_connection) != 0)
        {
            // No X server (Wayland, a headless box, an unset DISPLAY): nothing to read, and
            // nothing cached either - a session that starts X later must be picked up.
            DropConnection();
            return 0;
        }

        return _connection;
    }

    private nint DropConnection()
    {
        if (_connection != 0)
        {
            XcbNative.Disconnect(_connection);
            _connection = 0;
        }

        return 0;
    }

    private static nint RootWindow(nint connection)
    {
        var iterator = XcbNative.SetupRootsIterator(XcbNative.GetSetup(connection));

        return iterator.HasScreen ? Marshal.PtrToStructure<Screen>(iterator.Screen).Root : 0;
    }

    /// <summary>The active toplevel window, or 0 when the window manager names none.</summary>
    private static nint ActiveWindow(nint connection, nint root)
    {
        var atom = XcbNative.Intern(connection, ActiveWindowProperty);
        if (atom == XcbNative.AtomNone)
        {
            return 0;
        }

        var words = XcbNative.ReadWords(connection, root, atom);

        return words.Count > 0 ? (nint)words[0] : 0;
    }

    /// <summary>The process that owns the window, or 0 when the window names none.</summary>
    private static int ProcessId(nint connection, nint window)
    {
        var atom = XcbNative.Intern(connection, ProcessIdProperty);
        if (atom == XcbNative.AtomNone)
        {
            return 0;
        }

        var words = XcbNative.ReadWords(connection, window, atom);

        return words.Count > 0 ? (int)words[0] : 0;
    }

    /// <summary>
    /// Whether the window manager reports the window as fullscreen. Absent state reads as
    /// windowed: an unmanaged or ordinary window carries no <c>_NET_WM_STATE</c> at all, which is
    /// the common case, and a manager without EWMH support simply never says "fullscreen".
    /// </summary>
    private static bool IsFullscreen(nint connection, nint window)
    {
        var state = XcbNative.Intern(connection, WindowStateProperty);
        var fullscreen = XcbNative.Intern(connection, FullscreenState);

        if (state == XcbNative.AtomNone || fullscreen == XcbNative.AtomNone)
        {
            return false;
        }

        return XcbNative.ReadWords(connection, window, state).Contains(fullscreen);
    }

    /// <summary>
    /// The process name for a pid, read from <c>/proc</c>: the comm file, which is the kernel's
    /// own name for the task and carries no path or arguments. Null for a pid that is gone, or
    /// whose comm cannot be read - the window is on its way out in that case too.
    /// </summary>
    private static string? ProcessName(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            var name = File.ReadAllText($"/proc/{pid}/comm").Trim();
            return name.Length == 0 ? null : name;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DropConnection();
    }
}
