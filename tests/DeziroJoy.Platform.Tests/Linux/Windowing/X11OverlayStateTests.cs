using System.Runtime.InteropServices;
using DeziroJoy.Platform.Linux.Windowing;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux.Windowing;

/// <summary>
/// The EWMH above-state, pinned against a fake display: the read of the state property, the
/// merged write, the client message to the root window, the raise and the flush, in that order,
/// and the cleanup around them. These are the requests a window manager sees, so their shape -
/// which atom, which action word, which source word, which event mask - and what they leave of
/// the window's other state is what decides whether an overlay floats above the target
/// application or sits under it, and none of it can be left to "it works on my machine".
/// </summary>
public sealed class X11OverlayStateTests
{
    private const nint Window = 0x1234;

    /// <summary>SKIP_TASKBAR and SKIP_PAGER, the pair a taskbar-less floating window carries.</summary>
    private static readonly ulong[] TaskbarlessState = [0x2001, 0x2002];

    private static FakeX11OverlayApi Applied()
    {
        var fake = new FakeX11OverlayApi();
        fake.SetState(Window, TaskbarlessState);
        X11OverlayState.Apply(fake, Window);
        return fake;
    }

    [Fact]
    public void The_Above_State_Is_Merged_Into_The_Property_The_Window_Already_Has()
    {
        var fake = Applied();

        // The property is read before it is written, and the write is the merged list: the
        // atoms the window already carried stay, and ABOVE joins them.
        var read = Assert.Single(fake.Of<ReadAtomPropertyCall>());
        Assert.Equal(Window, read.Window);
        Assert.Equal(0x1001ul, read.Property);
        Assert.Equal(X11OverlayNative.XaAtom, read.Type);

        var property = Assert.Single(fake.Of<ReplaceAtomPropertyCall>());
        Assert.Equal(Window, property.Window);
        Assert.Equal(0x1001ul, property.Property);
        Assert.Equal(X11OverlayNative.XaAtom, property.Type);
        Assert.Equal([0x2001ul, 0x2002ul, 0x1002ul], property.Atoms);
    }

    [Fact]
    public void A_Write_That_Only_Held_The_Above_State_Would_Clear_The_Rest()
    {
        // The bug this guards against: replacing _NET_WM_STATE with ABOVE alone silently drops
        // SKIP_TASKBAR and its neighbours - the window would show up in the taskbar the moment
        // the window manager stopped healing the property behind the client's back.
        var fake = Applied();

        var property = Assert.Single(fake.Of<ReplaceAtomPropertyCall>());
        Assert.Contains(0x2001ul, property.Atoms);
        Assert.Contains(0x2002ul, property.Atoms);
    }

    [Fact]
    public void Adding_The_Above_State_Twice_Does_Not_Grow_The_List()
    {
        var fake = new FakeX11OverlayApi();
        fake.SetState(Window, TaskbarlessState);

        // Every appearance asks again; the second ask merges into what the first one wrote, so
        // the list holds ABOVE exactly once however often the window is shown.
        X11OverlayState.Apply(fake, Window);
        X11OverlayState.Apply(fake, Window);

        var writes = fake.Of<ReplaceAtomPropertyCall>();
        Assert.Equal(2, writes.Count);
        Assert.Equal([0x2001ul, 0x2002ul, 0x1002ul], writes[0].Atoms);
        Assert.Equal([0x2001ul, 0x2002ul, 0x1002ul], writes[1].Atoms);
    }

    [Fact]
    public void A_Property_The_Window_Does_Not_Have_Yet_Gets_The_Above_State()
    {
        var fake = new FakeX11OverlayApi();

        // No state staged: the window carries none, so the merged list is ABOVE alone - there
        // is nothing to preserve.
        X11OverlayState.Apply(fake, Window);

        var property = Assert.Single(fake.Of<ReplaceAtomPropertyCall>());
        Assert.Equal([0x1002ul], property.Atoms);
    }

    [Fact]
    public void A_Read_That_Cannot_Be_Trusted_Leaves_The_Property_Alone_But_Still_Asks()
    {
        var fake = new FakeX11OverlayApi();
        fake.SetState(Window, TaskbarlessState);
        fake.ReadFails = true;

        // A protocol error, a property of another shape: the merged list cannot be built, so the
        // property is left exactly as it is rather than written a guess. The message and the
        // raise still go out - the ask is never silently dropped, only the write is.
        X11OverlayState.Apply(fake, Window);

        Assert.Empty(fake.Of<ReplaceAtomPropertyCall>());
        Assert.Single(fake.Of<SendClientMessageCall>());
        Assert.Single(fake.Of<RaiseWindowCall>());
        Assert.Single(fake.Of<FlushCall>());
        Assert.Single(fake.Of<CloseDisplayCall>());
    }

    [Fact]
    public void The_Above_State_Is_Also_Requested_Of_The_Root_Window()
    {
        var fake = Applied();

        // The EWMH request itself, the only way the spec gives a client to change the state of
        // a mapped window: a client message to the root window naming the window it is about.
        var message = Assert.Single(fake.Of<SendClientMessageCall>());
        Assert.Equal((nint)0x8000, message.Recipient);
        Assert.Equal(Window, message.Window);
        Assert.Equal(0x1001ul, message.MessageType);

        // The words, in the spec's own order: add the state, the state is ABOVE, there is no
        // second state, the request comes from an application, and the last word is unused.
        Assert.Equal(
            [X11OverlayNative.NetWmStateAdd, 0x1002L, 0L, X11OverlayNative.SourceApplication, 0L],
            message.Data);

        // The mask a window manager selects on the root window; a narrower one reaches no
        // window manager at all, which is a request that silently does nothing.
        Assert.Equal(X11OverlayNative.RootEventMask, message.EventMask);
    }

    [Fact]
    public void The_Window_Is_Raised_And_The_Requests_Are_Flushed()
    {
        var fake = Applied();

        // A window manager that honours neither the property nor the message still sees the
        // window raised in its own stack, and the flush pushes every request out before the
        // connection closes behind it.
        Assert.Equal(Window, Assert.Single(fake.Of<RaiseWindowCall>()).Window);
        Assert.Single(fake.Of<FlushCall>());
    }

    [Fact]
    public void The_Dance_Runs_In_The_Order_A_Window_Manager_Sees_It()
    {
        var fake = Applied();

        Assert.Equal(
            [
                typeof(OpenDisplayCall),
                typeof(InternAtomCall),
                typeof(InternAtomCall),
                typeof(ReadAtomPropertyCall),
                typeof(ReplaceAtomPropertyCall),
                typeof(DefaultRootWindowCall),
                typeof(SendClientMessageCall),
                typeof(RaiseWindowCall),
                typeof(FlushCall),
                typeof(CloseDisplayCall),
            ],
            fake.Calls.Select(call => call.GetType()).ToArray());
    }

    [Fact]
    public void Both_Atoms_Are_Interned_Before_The_Property_Is_Touched()
    {
        var fake = Applied();

        // The state property first, then the state it is being set to: the read and the write
        // both need both atoms, and a server that refuses one of them must not see half a
        // request.
        Assert.Equal(
            ["_NET_WM_STATE", "_NET_WM_STATE_ABOVE"],
            fake.Of<InternAtomCall>().Select(atom => atom.Name).ToArray());

        var calls = fake.Calls.ToArray();
        Assert.True(
            Array.IndexOf(calls, fake.Of<InternAtomCall>()[1]) < Array.IndexOf(calls, fake.Of<ReadAtomPropertyCall>()[0]),
            "the above-state atom is interned before the property is read");
    }

    [Fact]
    public void Every_Application_Repeats_The_Whole_Dance()
    {
        var fake = new FakeX11OverlayApi();
        fake.SetState(Window, TaskbarlessState);

        // A hide-and-show cycle is a fresh mapping, and a window manager starts a newly mapped
        // window at the bottom of the stack again - so each application asks for everything.
        X11OverlayState.Apply(fake, Window);
        X11OverlayState.Apply(fake, Window);

        Assert.Equal(2, fake.Of<OpenDisplayCall>().Count);
        Assert.Equal(2, fake.Of<ReadAtomPropertyCall>().Count);
        Assert.Equal(2, fake.Of<ReplaceAtomPropertyCall>().Count);
        Assert.Equal(2, fake.Of<SendClientMessageCall>().Count);
        Assert.Equal(2, fake.Of<RaiseWindowCall>().Count);
        Assert.Equal(2, fake.Of<CloseDisplayCall>().Count);
    }

    [Fact]
    public void A_Window_Id_Of_Zero_Touches_Nothing()
    {
        var fake = new FakeX11OverlayApi();

        // A window with no platform handle yet: there is no X11 window to name, so nothing is
        // asked of anything - and no display is opened to ask it with.
        X11OverlayState.Apply(fake, 0);

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void A_Machine_With_No_X_Server_Leaves_The_Window_Alone()
    {
        var fake = new FakeX11OverlayApi { Display = 0 };

        // Wayland without XWayland, a headless box, an unset DISPLAY: the connect answers
        // nothing, and nothing was opened, so nothing is closed either.
        X11OverlayState.Apply(fake, Window);

        Assert.Equal([typeof(OpenDisplayCall)], fake.Calls.Select(call => call.GetType()).ToArray());
    }

    [Fact]
    public void A_Server_Without_The_Atoms_Still_Raises_The_Window()
    {
        var fake = new FakeX11OverlayApi();
        fake.Atoms.Remove("_NET_WM_STATE_ABOVE");

        // A server that names neither EWMH atom has no state to set and no manager to send a
        // message to; the raise is all that is left to ask for, and the display is still closed.
        X11OverlayState.Apply(fake, Window);

        Assert.Empty(fake.Of<ReadAtomPropertyCall>());
        Assert.Empty(fake.Of<ReplaceAtomPropertyCall>());
        Assert.Empty(fake.Of<SendClientMessageCall>());
        Assert.Single(fake.Of<RaiseWindowCall>());
        Assert.Single(fake.Of<FlushCall>());
        Assert.Single(fake.Of<CloseDisplayCall>());
    }

    [Fact]
    public void A_Missing_Library_Is_Swallowed_And_The_Display_Is_Still_Closed()
    {
        var fake = new FakeX11OverlayApi();
        fake.SetState(Window, TaskbarlessState);
        fake.ThrowOn(nameof(IX11OverlayApi.InternAtom), new DllNotFoundException("libX11.so.6"));

        // A Linux box without libX11: the window keeps whatever stacking and whatever state it
        // already has, and the connection that was opened is closed rather than leaked.
        X11OverlayState.Apply(fake, Window);

        Assert.Empty(fake.Of<ReadAtomPropertyCall>());
        Assert.Empty(fake.Of<ReplaceAtomPropertyCall>());
        Assert.Empty(fake.Of<SendClientMessageCall>());
        Assert.Empty(fake.Of<RaiseWindowCall>());
        Assert.Single(fake.Of<CloseDisplayCall>());
    }

    [Fact]
    public void A_Missing_Library_On_The_Read_Is_Swallowed_Too()
    {
        var fake = new FakeX11OverlayApi();
        fake.SetState(Window, TaskbarlessState);
        fake.ThrowOn(nameof(IX11OverlayApi.ReadAtomProperty), new EntryPointNotFoundException("XGetWindowProperty"));

        // The read is where the missing library would surface now; the failure stops the write
        // as well, and the connection is still closed.
        X11OverlayState.Apply(fake, Window);

        Assert.Empty(fake.Of<ReplaceAtomPropertyCall>());
        Assert.Empty(fake.Of<SendClientMessageCall>());
        Assert.Empty(fake.Of<RaiseWindowCall>());
        Assert.Single(fake.Of<CloseDisplayCall>());
    }

    [Fact]
    public void A_Missing_Library_On_The_Connect_Is_Swallowed_Too()
    {
        var fake = new FakeX11OverlayApi();
        fake.ThrowOn(nameof(IX11OverlayApi.OpenDisplay), new EntryPointNotFoundException("XOpenDisplay"));

        // Nothing was ever opened, so there is nothing to close - and the failure does not
        // reach the UI thread that asked for the above-state.
        X11OverlayState.Apply(fake, Window);

        Assert.Empty(fake.Of<CloseDisplayCall>());
    }

    [Fact]
    public void A_Server_Without_A_Root_Window_Skips_The_Message_But_Still_Merges()
    {
        var fake = new FakeX11OverlayApi { RootWindow = 0 };
        fake.SetState(Window, TaskbarlessState);

        // No root window to send the client message to: the property and the raise still go
        // out, and nothing is sent to a recipient that does not exist.
        X11OverlayState.Apply(fake, Window);

        Assert.Empty(fake.Of<SendClientMessageCall>());
        Assert.Single(fake.Of<ReplaceAtomPropertyCall>());
        Assert.Single(fake.Of<RaiseWindowCall>());
        Assert.Single(fake.Of<CloseDisplayCall>());
    }

    [Fact]
    public void The_Client_Message_Event_Is_Pinned_To_The_Xlib_Abi()
    {
        // XClientMessageEvent from Xlib.h, up to the five data words: type, serial, send_event,
        // display, window, message_type, format, then the data. The offsets were written out
        // rather than left to the compiler because a misaligned field sends a message the
        // window manager parses as garbage.
        Assert.Equal(96, Marshal.SizeOf<XClientMessageEvent>());
        Assert.Equal(0, Marshal.OffsetOf<XClientMessageEvent>(nameof(XClientMessageEvent.Type)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<XClientMessageEvent>(nameof(XClientMessageEvent.Window)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<XClientMessageEvent>(nameof(XClientMessageEvent.MessageType)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<XClientMessageEvent>(nameof(XClientMessageEvent.Data0)).ToInt32());
        Assert.Equal(88, Marshal.OffsetOf<XClientMessageEvent>(nameof(XClientMessageEvent.Data4)).ToInt32());
    }

    [Fact]
    public void The_Request_Constants_Are_The_Ones_The_Ewmh_Spec_Names()
    {
        // Pinned rather than trusted: the action word, the source word, the property type and
        // format, and the mask a window manager selects on the root window.
        Assert.Equal(1L, X11OverlayNative.NetWmStateAdd);
        Assert.Equal(1L, X11OverlayNative.SourceApplication);
        Assert.Equal(4ul, X11OverlayNative.XaAtom);
        Assert.Equal(32, X11OverlayNative.Format32);
        Assert.Equal(33, X11OverlayNative.ClientMessage);
        Assert.Equal(1L << 20, X11OverlayNative.SubstructureRedirectMask);
        Assert.Equal(1L << 19, X11OverlayNative.SubstructureNotifyMask);
        Assert.Equal((1L << 20) | (1L << 19), X11OverlayNative.RootEventMask);
    }
}
