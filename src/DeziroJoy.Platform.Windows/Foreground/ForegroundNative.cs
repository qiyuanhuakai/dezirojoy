using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Platform.Windows.Foreground;

/// <summary>
/// The foreground window, its process and whether it is fullscreen - the Win32 interop half of
/// <see cref="WindowsForegroundSource"/>. Only documented user32 exports are used:
/// <c>GetForegroundWindow</c>, <c>GetWindowThreadProcessId</c>, and the window-rect /
/// monitor-info pair the fullscreen check compares. Nothing here attaches threads, steals focus
/// or changes any window - it reads, which is why it is safe to run from the GUI's own timer
/// while a game has the foreground.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ForegroundNative
{
    /// <summary>MONITOR_DEFAULTTONEAREST: a window with no monitor of its own still resolves to one.</summary>
    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hwnd, out Rect window);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint monitor, nint info);

    /// <summary>
    /// The window's rectangle and the rectangle of the monitor it is on, either of which is null
    /// when the call failed. A failure is "unknown", not "not fullscreen".
    /// </summary>
    public static (Rect? Window, Rect? Monitor) Rectangles(nint hwnd)
    {
        Rect? window = GetWindowRect(hwnd, out var windowRect) ? windowRect : null;
        Rect? monitor = null;

        var handle = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (handle != nint.Zero)
        {
            var info = Marshal.AllocHGlobal(MonitorInfoLayout.Size);
            try
            {
                MonitorInfoLayout.Prepare(info);
                monitor = GetMonitorInfo(handle, info) ? MonitorInfoLayout.ReadMonitor(info) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }

        return (window, monitor);
    }

    /// <summary>
    /// Whether the window is fullscreen right now. A minimized window's rectangle is a
    /// sentinel rather than a screen, so it is excluded before the comparison rather than left
    /// to report a fullscreen state the user cannot see.
    /// </summary>
    public static bool IsFullscreen(nint hwnd)
    {
        var (window, monitor) = Rectangles(hwnd);

        return window is { } area && monitor is { } screen && ForegroundGeometry.CoversMonitor(area, screen);
    }

    /// <summary>
    /// The process name behind a window handle, without its .exe suffix - which is what
    /// <see cref="ForegroundRule.NormalizeProcessName"/> expects on both sides of a match. Null
    /// when the window cannot be resolved to a live process: it closed between the two reads, or
    /// this token may not query it (an elevated process, say). The window is on its way out in
    /// the first case, so a null reads as "no foreground program" and the desktop default runs.
    /// </summary>
    public static string? ProcessName(nint hwnd)
    {
        if (hwnd == nint.Zero || GetWindowThreadProcessId(hwnd, out var processId) == 0 || processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.IsNullOrEmpty(process.ProcessName) ? null : process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// The rectangle arithmetic the fullscreen decision is made of, and the rectangle itself.
/// Deliberately not platform-attributed: this is pure arithmetic over two rectangles, and the
/// rule table's "仅全屏" condition depends on it being right wherever the tests run - which is
/// why it is not folded into the Windows-only interop beside it.
/// </summary>
internal static class ForegroundGeometry
{
    /// <summary>
    /// Whether the window covers its whole monitor. A borderless window that merely spans the
    /// work area - a maximized window beside a taskbar, or a snapped one - is deliberately not
    /// fullscreen: the comparison is against the monitor's full rectangle. Both rectangles must
    /// be known, because a window that closed mid-read reports a rect that means nothing.
    /// </summary>
    public static bool CoversMonitor(Rect window, Rect monitor)
        => window.Left <= monitor.Left
        && window.Top <= monitor.Top
        && window.Right >= monitor.Right
        && window.Bottom >= monitor.Bottom;
}

/// <summary>A Win32 RECT: left, top, right, bottom in screen coordinates.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;

    public int Top;

    public int Right;

    public int Bottom;
}

/// <summary>
/// MONITORINFO laid out by hand. The struct route is a trap here: a <c>char[]</c> field is
/// marshalled as one byte per character under the default charset, which yields a 72-byte struct
/// on every platform - and <c>GetMonitorInfo</c> would then read the monitor rectangle out of the
/// wrong bytes and call a windowed application fullscreen. The ABI fixes the layout - cbSize,
/// rcMonitor, rcWork, dwFlags, then CCHDEVICENAME (32) UTF-16 characters - so the offsets are
/// spelled out against a buffer of exactly that size instead.
/// </summary>
internal static class MonitorInfoLayout
{
    /// <summary>cbSize (4) + two RECTs (32) + dwFlags (4) + 32 UTF-16 characters (64).</summary>
    public const int Size = 104;

    /// <summary>rcMonitor starts after cbSize.</summary>
    private const int MonitorOffset = 4;

    /// <summary>Writes cbSize, the one field the call validates before it reads anything.</summary>
    public static void Prepare(nint info) => Marshal.WriteInt32(info, 0, Size);

    public static Rect ReadMonitor(nint info) => new()
    {
        Left = Marshal.ReadInt32(info, MonitorOffset),
        Top = Marshal.ReadInt32(info, MonitorOffset + 4),
        Right = Marshal.ReadInt32(info, MonitorOffset + 8),
        Bottom = Marshal.ReadInt32(info, MonitorOffset + 12),
    };
}
