using System.Runtime.InteropServices;

namespace DeziroJoy.Platform.Linux.Windowing;

/// <summary>
/// The native surface the above-state talks through, behind an interface the same way the evdev
/// provider hides libc: the whole EWMH dance - intern the atoms, read the state property, write
/// the merged list back, send the client message, raise, flush - can then be driven
/// deterministically by tests, which record every call and can fail any one of them, so the
/// ordering and the failure cleanup are pinned without a window manager. Production wires
/// <see cref="X11OverlayApi"/>; every method keeps the exact contract of the libX11 call it
/// wraps.
/// </summary>
internal interface IX11OverlayApi
{
    /// <summary>
    /// XOpenDisplay over the display name the environment names, or 0 when nothing answers -
    /// no DISPLAY, a Wayland session with no XWayland bridge, a headless box. Zero is the one
    /// answer that means "there is no X server here", and it is what every other failure is
    /// reported as too: the caller has nothing to talk to either way.
    /// </summary>
    nint OpenDisplay();

    /// <summary>XCloseDisplay. A display that never opened is never closed.</summary>
    void CloseDisplay(nint display);

    /// <summary>The root window of the display's default screen; 0 when there is none.</summary>
    nint DefaultRootWindow(nint display);

    /// <summary>XInternAtom without only_if_exists, or <see cref="X11OverlayNative.AtomNone"/> when the server refuses.</summary>
    ulong InternAtom(nint display, string name);

    /// <summary>
    /// Reads one atom-list property (type <see cref="X11OverlayNative.XaAtom"/>, format 32).
    /// Returns the atoms the window already carries, an empty list when it carries none, and
    /// null when the read cannot be trusted - a protocol error, a property of another type or
    /// width, or one larger than one round trip - because a caller that merged a partial list
    /// would write the missing atoms away.
    /// </summary>
    IReadOnlyList<uint>? ReadAtomProperty(nint display, nint window, ulong property, ulong type);

    /// <summary>
    /// XChangeProperty in PropModeReplace over an atom list: the property named by
    /// <paramref name="property"/> ends up holding exactly <paramref name="atoms"/> of type
    /// <paramref name="type"/>, which is how <c>_NET_WM_STATE</c> is written. The atoms are
    /// the merged list - what the window already carried plus the above-state - never a
    /// single-atom replacement.
    /// </summary>
    void ReplaceAtomProperty(nint display, nint window, ulong property, ulong type, ulong[] atoms);

    /// <summary>
    /// XSendEvent of a ClientMessage: <paramref name="messageType"/> naming the protocol,
    /// <paramref name="data"/> its words, delivered to <paramref name="recipient"/> - the root
    /// window, the EWMH path - under <paramref name="eventMask"/>, with
    /// <paramref name="window"/> recorded in the event as the window the message is about.
    /// </summary>
    void SendClientMessage(
        nint display,
        nint recipient,
        nint window,
        ulong messageType,
        long[] data,
        long eventMask);

    /// <summary>XRaiseWindow: the window moves to the top of its own stack.</summary>
    void RaiseWindow(nint display, nint window);

    /// <summary>XFlush: every request above leaves for the server now, not at the next read.</summary>
    void Flush(nint display);
}

/// <summary>
/// The production <see cref="IX11OverlayApi"/>: every call is the libX11 P/Invoke it wraps. One
/// connection per <see cref="X11OverlayState.Apply"/> call, opened and closed around the three
/// requests - the same one-shot pattern the cursor probe uses, because an overlay that asks to
/// be above twice a second has no use for a resident connection, and a connection that outlives
/// the window it was opened for is a connection nothing closes.
/// </summary>
internal sealed class X11OverlayApi : IX11OverlayApi
{
    public static IX11OverlayApi Shared { get; } = new X11OverlayApi();

    public nint OpenDisplay() => X11OverlayNative.OpenDisplay(0);

    public void CloseDisplay(nint display) => X11OverlayNative.CloseDisplay(display);

    public nint DefaultRootWindow(nint display) => X11OverlayNative.DefaultRootWindow(display);

    public ulong InternAtom(nint display, string name)
        => X11OverlayNative.InternAtom(display, name, false);

    public IReadOnlyList<uint>? ReadAtomProperty(nint display, nint window, ulong property, ulong type)
    {
        var status = X11OverlayNative.GetWindowProperty(
            display,
            window,
            property,
            0,
            X11OverlayNative.MaxPropertyWords,
            false,
            type,
            out var actualType,
            out var format,
            out var items,
            out var bytesAfter,
            out var data);

        // A protocol error - a window that went away under the ask - reads as "cannot be
        // trusted", not as "no atoms".
        if (status != 0)
        {
            return null;
        }

        if (data == 0)
        {
            // No such property: the window carries no state at all, which is an empty list
            // rather than a failed read.
            return [];
        }

        try
        {
            // Anything but the atom list this client knows how to merge - another type, another
            // width, or more atoms than one round trip carried - reads as untrustworthy too.
            if (format != X11OverlayNative.Format32 || actualType != type || bytesAfter != 0)
            {
                return null;
            }

            // Xlib hands format-32 data back one long per unit - eight bytes on every platform
            // Xlib builds for - so the stride is the slot, not the unit: reading four bytes at
            // a time would take the padding halves and answer [first atom, 0, 0, ...], which a
            // merge would then write back as real atoms.
            var atoms = new uint[items];
            for (nuint i = 0; i < items; i++)
            {
                atoms[i] = (uint)Marshal.ReadInt64(data, (int)(i * sizeof(long)));
            }

            return atoms;
        }
        finally
        {
            X11OverlayNative.Free(data);
        }
    }

    public void ReplaceAtomProperty(nint display, nint window, ulong property, ulong type, ulong[] atoms)
        => X11OverlayNative.ChangeProperty(
            display,
            window,
            property,
            type,
            X11OverlayNative.Format32,
            X11OverlayNative.PropModeReplace,
            atoms,
            atoms.Length);

    public void SendClientMessage(
        nint display,
        nint recipient,
        nint window,
        ulong messageType,
        long[] data,
        long eventMask)
    {
        var clientMessage = new XClientMessageEvent
        {
            Type = X11OverlayNative.ClientMessage,
            SendEvent = 1,
            Display = display,
            Window = window,
            MessageType = messageType,
            Format = X11OverlayNative.Format32,
            Data0 = data.Length > 0 ? data[0] : 0,
            Data1 = data.Length > 1 ? data[1] : 0,
            Data2 = data.Length > 2 ? data[2] : 0,
            Data3 = data.Length > 3 ? data[3] : 0,
            Data4 = data.Length > 4 ? data[4] : 0,
        };

        _ = X11OverlayNative.SendEvent(display, recipient, false, eventMask, ref clientMessage);
    }

    public void RaiseWindow(nint display, nint window) => X11OverlayNative.RaiseWindow(display, window);

    public void Flush(nint display) => _ = X11OverlayNative.Flush(display);
}
