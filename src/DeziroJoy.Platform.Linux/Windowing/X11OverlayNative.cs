using System.Runtime.InteropServices;

namespace DeziroJoy.Platform.Linux.Windowing;

/// <summary>
/// The libX11 calls the above-state needs: open a display, intern the EWMH atoms, read the
/// window's <c>_NET_WM_STATE</c> property, write the merged list back, send the matching client
/// message to the root window, raise the window and flush.
/// <para>
/// Xlib rather than XCB, unlike the foreground source: there is no polling loop here to protect,
/// every call is a one-shot request issued from the UI thread and checked by return value, and
/// the process already has an Xlib-loaded platform under it - Avalonia's X11 backend - which
/// also installs a process-global error handler, so a protocol error from one of these requests
/// is recorded and ignored instead of ending the process.
/// </para>
/// <para>
/// Every entry point is reached through <see cref="IX11OverlayApi"/> so the whole dance can be
/// driven without a display; this file holds the marshalling and the ABI facts (the event mask,
/// the property mode, the client-message layout) nothing else should have to know.
/// </para>
/// </summary>
internal static partial class X11OverlayNative
{
    /// <summary>X11's None: the atom the server does not know.</summary>
    public const ulong AtomNone = 0;

    /// <summary>XA_ATOM, the property type <c>_NET_WM_STATE</c> holds: a list of atoms.</summary>
    public const ulong XaAtom = 4;

    /// <summary>PropModeReplace: the property takes exactly the atoms given, no more.</summary>
    public const int PropModeReplace = 0;

    /// <summary>_NET_WM_STATE_ADD: the client message's action word.</summary>
    public const long NetWmStateAdd = 1;

    /// <summary>
    /// The EWMH source indication for a normal application, so the window manager can tell an
    /// application's own request from a pager acting on the user's behalf.
    /// </summary>
    public const long SourceApplication = 1;

    /// <summary>
    /// The event mask the client message carries. A window manager selects
    /// SubstructureRedirectMask on the root window - that is how it intercepts map and
    /// configure requests - so a message sent with any narrower mask reaches no window manager
    /// at all. SubstructureNotifyMask rides along with it, the way the EWMH examples send it.
    /// </summary>
    public const long SubstructureRedirectMask = 1L << 20;

    public const long SubstructureNotifyMask = 1L << 19;

    /// <summary>The event mask <see cref="SendEvent"/> is asked to deliver with.</summary>
    public const long RootEventMask = SubstructureRedirectMask | SubstructureNotifyMask;

    /// <summary>Xlib's ClientMessage event type number, from X.h.</summary>
    public const int ClientMessage = 33;

    /// <summary>
    /// The property format <c>_NET_WM_STATE</c> and its client message both use: 32-bit units,
    /// so one atom or one data word per unit.
    /// </summary>
    public const int Format32 = 32;

    /// <summary>
    /// How many 32-bit words one property read asks for. <c>_NET_WM_STATE</c> holds a handful of
    /// atoms; a read that leaves bytes over is a property this client cannot merge safely, and
    /// the caller must not write a truncated list back.
    /// </summary>
    public const int MaxPropertyWords = 1024;

    [LibraryImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
    public static partial nint OpenDisplay(nint displayName);

    [LibraryImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
    public static partial void CloseDisplay(nint display);

    [LibraryImport("libX11.so.6", EntryPoint = "XDefaultRootWindow")]
    public static partial nint DefaultRootWindow(nint display);

    [LibraryImport("libX11.so.6", EntryPoint = "XInternAtom", StringMarshalling = StringMarshalling.Utf8)]
    public static partial ulong InternAtom(nint display, string atomName, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [LibraryImport("libX11.so.6", EntryPoint = "XGetWindowProperty")]
    public static partial int GetWindowProperty(
        nint display,
        nint window,
        ulong property,
        long longOffset,
        long longLength,
        [MarshalAs(UnmanagedType.Bool)] bool delete,
        ulong reqType,
        out ulong actualTypeReturn,
        out int actualFormatReturn,
        out nuint nitemsReturn,
        out nuint bytesAfterReturn,
        out nint propReturn);

    [LibraryImport("libX11.so.6", EntryPoint = "XFree")]
    public static partial void Free(nint pointer);

    /// <summary>
    /// XChangeProperty over an atom list: the buffer is one <see cref="ulong"/> per atom, the
    /// long array Xlib asks format-32 data for - the wire unit is 32 bits, but Xlib reads the
    /// caller's buffer eight bytes per element and packs each long's low word, so an element
    /// narrower than the format would have Xlib pair the caller's atoms up and pack heap
    /// garbage into the rest. Each atom fits a long's low word on every byte order, which is
    /// what makes the wide buffer the endianness-safe one.
    /// </summary>
    [LibraryImport("libX11.so.6", EntryPoint = "XChangeProperty")]
    public static partial int ChangeProperty(
        nint display,
        nint window,
        ulong property,
        ulong type,
        int format,
        int mode,
        ulong[] data,
        int elements);

    [LibraryImport("libX11.so.6", EntryPoint = "XSendEvent")]
    public static partial int SendEvent(
        nint display,
        nint window,
        [MarshalAs(UnmanagedType.Bool)] bool propagate,
        long eventMask,
        ref XClientMessageEvent clientMessage);

    [LibraryImport("libX11.so.6", EntryPoint = "XRaiseWindow")]
    public static partial void RaiseWindow(nint display, nint window);

    [LibraryImport("libX11.so.6", EntryPoint = "XFlush")]
    public static partial int Flush(nint display);
}

/// <summary>
/// XClientMessageEvent from Xlib.h, with the field offsets written out rather than left to the
/// compiler: the union member XSendEvent reads is the one whose first word says ClientMessage,
/// and every offset past it is where Xlib's own header puts it. A misaligned field sends a
/// message the window manager parses as garbage.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct XClientMessageEvent
{
    [FieldOffset(0)] public int Type;

    [FieldOffset(8)] public ulong Serial;

    /// <summary>Xlib's Bool, which is an int: 1 for an event sent by another client.</summary>
    [FieldOffset(16)] public int SendEvent;

    [FieldOffset(24)] public nint Display;

    /// <summary>The window the message is about - the client's own window, not the root.</summary>
    [FieldOffset(32)] public nint Window;

    /// <summary>The atom naming the protocol: <c>_NET_WM_STATE</c>.</summary>
    [FieldOffset(40)] public ulong MessageType;

    [FieldOffset(48)] public int Format;

    /// <summary>The five message words: action, first state, second state, source, unused.</summary>
    [FieldOffset(56)] public long Data0;

    [FieldOffset(64)] public long Data1;

    [FieldOffset(72)] public long Data2;

    [FieldOffset(80)] public long Data3;

    [FieldOffset(88)] public long Data4;
}
