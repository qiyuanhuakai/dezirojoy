using System.Runtime.InteropServices;
using System.Text;

namespace DeziroJoy.Platform.Linux.Foreground;

/// <summary>
/// XCB interop for the foreground query: connect, read the EWMH <c>_NET_ACTIVE_WINDOW</c> off
/// the root window, then read that window's <c>_NET_WM_PID</c> and <c>_NET_WM_STATE</c>. The
/// picker reads the same way over the whole desktop: <c>_NET_CLIENT_LIST</c> for the windows
/// that are open, and each one's <c>_NET_WM_PID</c> and <c>_NET_WM_NAME</c> beside them.
/// XCB rather than Xlib deliberately: Xlib's default handler exits the process on a fatal I/O
/// error, and a GUI that polls the foreground must not die because the X server went away.
/// XCB reports the failure as a return code instead, which is all a best-effort read needs.
/// <para>
/// An XCB cookie is one 32-bit sequence number, so it travels as a plain <see cref="uint"/>
/// rather than through a one-field struct that would only mirror the ABI.
/// </para>
/// </summary>
internal static partial class XcbNative
{
    /// <summary>XCB_ATOM_NONE: the property or atom does not exist.</summary>
    public const uint AtomNone = 0;

    [LibraryImport("libxcb.so.1", EntryPoint = "xcb_connect")]
    public static partial nint Connect(nint displayName, nint screenp);

    [LibraryImport("libxcb.so.1", EntryPoint = "xcb_disconnect")]
    public static partial void Disconnect(nint connection);

    [LibraryImport("libxcb.so.1", EntryPoint = "xcb_connection_has_error")]
    public static partial int ConnectionHasError(nint connection);

    [LibraryImport("libxcb.so.1", EntryPoint = "xcb_get_setup")]
    public static partial nint GetSetup(nint connection);

    [LibraryImport("libxcb.so.1", EntryPoint = "xcb_setup_roots_iterator")]
    public static partial ScreenIterator SetupRootsIterator(nint setup);

    [LibraryImport("libxcb.so.1", EntryPoint = "xcb_intern_atom", StringMarshalling = StringMarshalling.Utf8)]
    public static partial uint InternAtom(nint connection, byte onlyIfExists, ushort nameLength, string name);

    [LibraryImport("libxcb.so.1", EntryPoint = "xcb_intern_atom_reply")]
    public static partial nint InternAtomReply(nint connection, uint cookie, nint error);

    [LibraryImport("libxcb.so.1", EntryPoint = "xcb_get_property")]
    public static partial uint GetProperty(
        nint connection,
        byte delete,
        nint window,
        nint property,
        nint type,
        uint longOffset,
        uint longLength);

    [LibraryImport("libxcb.so.1", EntryPoint = "xcb_get_property_reply")]
    public static partial nint GetPropertyReply(nint connection, uint cookie, nint error);

    [LibraryImport("libc.so.6", EntryPoint = "free")]
    public static partial void Free(nint pointer);

    /// <summary>
    /// The reply header's size: response_type, format, sequence, length, type, bytes_after,
    /// value_len and twelve pad bytes - no pointer or long in it, so 32 bytes everywhere XCB
    /// runs. The property value follows immediately after it.
    /// </summary>
    public const int ReplyHeaderSize = 32;

    /// <summary>
    /// Where each field of <c>xcb_get_property_reply_t</c> sits, straight from xproto.h. Spelled
    /// out because the reply carries the property's width and length in the two places a guess
    /// gets wrong: format is a single byte at offset 1, and value_len counts format units at 16.
    /// Reading either from the wrong byte returns an empty property that looks like a missing
    /// one.
    /// </summary>
    public const int ReplyFormatOffset = 1;

    public const int ReplyBytesAfterOffset = 12;

    public const int ReplyValueLenOffset = 16;

    /// <summary>Where the property's own bytes start: immediately after the 32-byte header.</summary>
    public const int ReplyValueOffset = 32;

    /// <summary>
    /// 32-bit words requested per round trip. A long_length of zero asks the server for the
    /// length only and returns no data at all - the trap this bound exists to avoid - and an
    /// unbounded one can exceed the maximum request size, so the read walks the property in
    /// chunks instead.
    /// </summary>
    public const uint WordsPerRead = 256;

    /// <summary>
    /// Every 32-bit word one property holds, in order, or an empty list when it holds none.
    /// Reads in chunks until the server reports nothing left over, so a property larger than
    /// one round trip still comes back whole.
    /// </summary>
    public static List<uint> ReadWords(nint connection, nint window, uint property)
    {
        var words = new List<uint>();
        uint offset = 0;

        while (true)
        {
            var cookie = GetProperty(connection, 0, window, (nint)property, 0, offset, WordsPerRead);
            var reply = GetPropertyReply(connection, cookie, 0);

            if (reply == 0)
            {
                break;
            }

            try
            {
                // value_len (offset 16) counts format units in this reply; format (offset 1) is
                // their width, so a format-16 property is two words per unit.
                var units = Marshal.ReadInt32(reply, ReplyValueLenOffset);
                var format = Marshal.ReadByte(reply, ReplyFormatOffset);
                var wordsInReply = format == 32 ? units : units / 2;

                for (var i = 0; i < wordsInReply; i++)
                {
                    words.Add((uint)Marshal.ReadInt32(reply, ReplyValueOffset + (i * 4)));
                }

                var bytesAfter = (uint)Marshal.ReadInt32(reply, ReplyBytesAfterOffset);

                // A reply that carries nothing yet leaves bytes over would re-read itself
                // forever; the server never sends one, and stopping costs the rest of the
                // property rather than the thread that is walking it.
                var advance = WordsConsumed(units, format);

                if (bytesAfter == 0 || advance == 0)
                {
                    break;
                }

                offset += advance;
            }
            finally
            {
                Free(reply);
            }
        }

        return words;
    }

    /// <summary>
    /// One string property, read in chunks exactly as <see cref="ReadWords"/> reads words - a
    /// window title can outrun one round trip. The value is format units of the property's own
    /// width, and 8 is the only width a string has: anything else reads as an absent property
    /// rather than as misparsed bytes, and a property the server refuses returns the empty
    /// string rather than throwing.
    /// </summary>
    public static string ReadString(nint connection, nint window, uint property)
    {
        var bytes = new List<byte>();
        uint offset = 0;

        while (true)
        {
            var cookie = GetProperty(connection, 0, window, (nint)property, 0, offset, WordsPerRead);
            var reply = GetPropertyReply(connection, cookie, 0);

            if (reply == 0)
            {
                break;
            }

            try
            {
                var units = Marshal.ReadInt32(reply, ReplyValueLenOffset);
                var format = Marshal.ReadByte(reply, ReplyFormatOffset);

                if (format != 8)
                {
                    break;
                }

                for (var i = 0; i < units; i++)
                {
                    bytes.Add(Marshal.ReadByte(reply, ReplyValueOffset + i));
                }

                var bytesAfter = (uint)Marshal.ReadInt32(reply, ReplyBytesAfterOffset);

                // The same stop ReadWords makes: a reply that carries nothing and leaves bytes
                // over would re-read itself forever, and stopping costs the rest of the title
                // rather than the thread that is reading it.
                var advance = WordsConsumed(units, format);

                if (bytesAfter == 0 || advance == 0)
                {
                    break;
                }

                offset += advance;
            }
            finally
            {
                Free(reply);
            }
        }

        // X strings are NUL-terminated without counting the terminator, so nothing is normally
        // left over; trimming keeps a reply that carries one from ending the title in nothing.
        return Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(bytes)).TrimEnd('\0');
    }

    /// <summary>
    /// How many 32-bit words a reply of <paramref name="units"/> format units consumed. The
    /// request's long_offset counts words whatever the format - the one place a chunked read
    /// gets it wrong - so a property of 1024 bytes is 256 words, and advancing by units instead
    /// would re-read the same bytes and never reach the end.
    /// </summary>
    public static uint WordsConsumed(int units, byte format)
        => (uint)((units * (format / 8) + 3) / 4);

    /// <summary>The atom a reply names, or <see cref="AtomNone"/> when there is no reply.</summary>
    public static uint AtomOf(nint reply)
        => reply == 0 ? AtomNone : (uint)Marshal.ReadInt32(reply, 8);

    /// <summary>Interns one EWMH atom, or <see cref="AtomNone"/> when the server refuses.</summary>
    public static uint Intern(nint connection, string name)
    {
        var cookie = InternAtom(connection, 0, (ushort)name.Length, name);
        var reply = InternAtomReply(connection, cookie, 0);

        if (reply == 0)
        {
            return AtomNone;
        }

        var atom = AtomOf(reply);
        Free(reply);

        return atom;
    }
}

/// <summary>
/// xcb_screen_iterator_t. <see cref="Screen"/> is read through its data pointer; only the root
/// window is needed, and it is the screen's first field.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ScreenIterator(nint data, int rem, int index)
{
    private readonly nint _data = data;

    private readonly int _rem = rem;

    private readonly int _index = index;

    public bool HasScreen => _data != 0 && _rem > 0;

    public nint Screen => _data;
}

/// <summary>xcb_screen_t, up to the fields this read needs: the root window.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Screen(nint root)
{
    private readonly nint _root = root;

    public nint Root => _root;
}
