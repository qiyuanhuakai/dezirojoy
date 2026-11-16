namespace DeziroJoy.Platform.Linux.Windowing;

/// <summary>
/// Keeps one X11 window above the others, the EWMH way: <c>_NET_WM_STATE_ABOVE</c>.
/// <para>
/// Why a floating window needs this at all: a toolkit's own topmost flag is applied when the
/// property is set, which for a window declared topmost in XAML is before it is mapped - and a
/// window manager only honours <c>_NET_WM_STATE_ABOVE</c> once it manages the mapped window.
/// The state is therefore asked for again from <see cref="Apply"/>, which runs once the window
/// exists: the window's <c>_NET_WM_STATE</c> is read, ABOVE is merged into the list it already
/// holds - SKIP_TASKBAR and its neighbours, which the toolkit set for this very window, are
/// kept - and the merged list is written back; the client message is sent so a manager that
/// only acts on requests restacks the window; and the window is raised in its own stack and
/// flushed for the managers that do neither.
/// </para>
/// <para>
/// The read is what keeps the write honest: a client that replaced the property with ABOVE
/// alone would silently clear every other state the window carries. A read that cannot be
/// trusted - a protocol error, a property of another shape - leaves the property untouched
/// entirely, and the message and the raise still carry the request.
/// </para>
/// <para>
/// Three things this deliberately does not do: it never asks for focus (no
/// <c>_NET_ACTIVE_WINDOW</c>, no <c>XSetInputFocus</c> - the caller's windows are
/// show-without-activation overlays, and taking focus would defeat them), it touches no input
/// region (click-through stays exactly as the window was built), and it names no window but the
/// one it is given, so nothing else on the desktop is activated or raised.
/// </para>
/// <para>
/// Best-effort by design: a window id of zero, a machine with no X server, a server that knows
/// neither atom, or a box without libX11 all leave the window wherever it was rather than
/// raising anything, and the display connection is closed on every path out of
/// <see cref="Apply"/>.
/// </para>
/// </summary>
public static class X11OverlayState
{
    private const string StateProperty = "_NET_WM_STATE";

    private const string AboveState = "_NET_WM_STATE_ABOVE";

    /// <summary>
    /// Applies the above-state to the window named by its X11 window id - the handle Avalonia's
    /// X11 backend reports. Safe to call for any id, on any thread, any number of times: the
    /// calls are requests, and repeating them is what keeps a window above across the
    /// hide-and-show cycles a mapping hint lives through.
    /// </summary>
    public static void Apply(nint window) => Apply(X11OverlayApi.Shared, window);

    /// <summary>
    /// The same dance over an injected api, which is how the tests drive it: a fake records the
    /// read, the property write, the client message and the raise in order, and fails any one of
    /// them, so the ordering and the cleanup are pinned without a display.
    /// </summary>
    internal static void Apply(IX11OverlayApi api, nint window)
    {
        if (window == 0)
        {
            return;
        }

        nint display = 0;

        try
        {
            display = api.OpenDisplay();

            if (display == 0)
            {
                // No X server here - Wayland without XWayland, a headless box, an unset
                // DISPLAY. Nothing was opened, so there is nothing to close.
                return;
            }

            ApplyOnDisplay(api, display, window);
        }
        catch (Exception exception) when (exception
            is DllNotFoundException or EntryPointNotFoundException)
        {
            // A Linux box without libX11: the window keeps whatever stacking it already has.
        }
        finally
        {
            if (display != 0)
            {
                api.CloseDisplay(display);
            }
        }
    }

    private static void ApplyOnDisplay(IX11OverlayApi api, nint display, nint window)
    {
        var state = api.InternAtom(display, StateProperty);
        var above = api.InternAtom(display, AboveState);

        // A server that names neither atom is not an EWMH one: there is no state to set and no
        // manager to send a message to, and the raise below is all that is left to ask for.
        if (state == X11OverlayNative.AtomNone || above == X11OverlayNative.AtomNone)
        {
            api.RaiseWindow(display, window);
            api.Flush(display);
            return;
        }

        // The property, read-modify-write: the atoms the window already carries stay, and ABOVE
        // joins them once. Writing ABOVE alone instead would clear every other state - the
        // SKIP_TASKBAR and SKIP_PAGER the toolkit set for this window among them - and a read
        // that cannot be trusted leaves the property alone entirely: the message and the raise
        // below still carry the request, so the ask is never silently dropped.
        if (api.ReadAtomProperty(display, window, state, X11OverlayNative.XaAtom) is { } current)
        {
            api.ReplaceAtomProperty(display, window, state, X11OverlayNative.XaAtom, Merge(current, above));
        }

        // Then the EWMH request itself: a client message to the root window, which is the only
        // way the spec gives a client to change the state of a mapped window. The action word
        // is _NET_WM_STATE_ADD, the first state word is ABOVE, the second is zero because there
        // is no second state to change, and the source word names an application so the manager
        // can weigh the request the way it weighs any other application's.
        var root = api.DefaultRootWindow(display);

        if (root != 0)
        {
            api.SendClientMessage(
                display,
                root,
                window,
                state,
                [
                    X11OverlayNative.NetWmStateAdd,
                    (long)above,
                    0,
                    X11OverlayNative.SourceApplication,
                    0,
                ],
                X11OverlayNative.RootEventMask);
        }

        // And raise the window in its own stack: a manager that honours neither the property nor
        // the message still leaves the overlay above its own group, and the flush pushes all of
        // it out before the connection closes behind it.
        api.RaiseWindow(display, window);
        api.Flush(display);
    }

    /// <summary>
    /// The window's state atoms with <paramref name="above"/> among them, the atoms it already
    /// had in their own order: adding a state the window already carries changes nothing, so
    /// repeating the ask never grows the list.
    /// </summary>
    private static ulong[] Merge(IReadOnlyList<uint> current, ulong above)
    {
        var atoms = new List<ulong>(current.Select(atom => (ulong)atom));

        if (!atoms.Contains(above))
        {
            atoms.Add(above);
        }

        return [.. atoms];
    }
}
