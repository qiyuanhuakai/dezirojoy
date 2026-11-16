using DeziroJoy.Platform.Linux.Windowing;

namespace DeziroJoy.Platform.Tests.Linux.Windowing;

/// <summary>
/// One recorded libX11 call, kept as data so a test can assert on the whole dance - which
/// calls were made, in what order, with which arguments - the way a window manager would see
/// it, without a window manager.
/// </summary>
internal abstract record X11OverlayCall;

internal sealed record OpenDisplayCall : X11OverlayCall;

internal sealed record CloseDisplayCall(nint Display) : X11OverlayCall;

internal sealed record InternAtomCall(string Name) : X11OverlayCall;

internal sealed record DefaultRootWindowCall(nint Display) : X11OverlayCall;

internal sealed record ReadAtomPropertyCall(
    nint Window,
    ulong Property,
    ulong Type) : X11OverlayCall;

internal sealed record ReplaceAtomPropertyCall(
    nint Window,
    ulong Property,
    ulong Type,
    IReadOnlyList<ulong> Atoms) : X11OverlayCall;

internal sealed record SendClientMessageCall(
    nint Recipient,
    nint Window,
    ulong MessageType,
    IReadOnlyList<long> Data,
    long EventMask) : X11OverlayCall;

internal sealed record RaiseWindowCall(nint Window) : X11OverlayCall;

internal sealed record FlushCall : X11OverlayCall;

/// <summary>
/// A controllable stand-in for the libX11 calls the above-state makes: it answers atoms from a
/// configurable table, hands out one display number, serves one state property per window from
/// <see cref="StateAtoms"/>, and records every call in <see cref="Calls"/> in order. Any single
/// call can be made to fail the way a missing library does - by throwing - and the property
/// read can be made to answer "cannot be trusted", so the merge and the conservative fallback
/// are both pinned: a display that was opened is always closed, a property the read could not
/// vouch for is never written, and a call that was never reached is never made.
/// </summary>
internal sealed class FakeX11OverlayApi : IX11OverlayApi
{
    private readonly Lock _sync = new();
    private readonly List<X11OverlayCall> _calls = [];

    /// <summary>The atom numbers the fake hands out for interned names, by name.</summary>
    public Dictionary<string, ulong> Atoms { get; } = new(StringComparer.Ordinal)
    {
        ["_NET_WM_STATE"] = 0x1001,
        ["_NET_WM_STATE_ABOVE"] = 0x1002,
        ["_NET_WM_STATE_SKIP_TASKBAR"] = 0x2001,
        ["_NET_WM_STATE_SKIP_PAGER"] = 0x2002,
    };

    /// <summary>
    /// What the state property holds per window, the atoms the toolkit already put there. The
    /// default is the pair a taskbar-less floating window carries; a test clears it to stage a
    /// window with no state yet, or fills it to stage one that is already above.
    /// </summary>
    public Dictionary<nint, ulong[]> StateAtoms { get; } = [];

    /// <summary>When set, the property read answers "cannot be trusted" (null) instead of atoms.</summary>
    public bool ReadFails { get; set; }

    /// <summary>When set, the next armed call throws this and clears it.</summary>
    public Exception? ThrowOnNext { get; set; }

    /// <summary>The method name <see cref="ThrowOnNext"/> is armed against, or null for any call.</summary>
    public string? ThrowOnNextMethod { get; set; }

    /// <summary>What OpenDisplay answers; 0 means "no X server here".</summary>
    public nint Display { get; set; } = 0x7000;

    /// <summary>What DefaultRootWindow answers; 0 means "no root window".</summary>
    public nint RootWindow { get; set; } = 0x8000;

    /// <summary>Every call the fake has served, in order.</summary>
    public IReadOnlyList<X11OverlayCall> Calls
    {
        get
        {
            lock (_sync)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<T> Of<T>() where T : X11OverlayCall
        => Calls.OfType<T>().ToArray();

    /// <summary>Arms one method to throw <paramref name="exception"/> on its next call.</summary>
    public void ThrowOn(string method, Exception exception)
    {
        ThrowOnNextMethod = method;
        ThrowOnNext = exception;
    }

    /// <summary>Stages the state property <paramref name="window"/> carries before the ask.</summary>
    public void SetState(nint window, params ulong[] atoms)
    {
        StateAtoms[window] = atoms;
    }

    public nint OpenDisplay()
    {
        if (Record(new OpenDisplayCall()) is false)
        {
            return 0;
        }

        return Display;
    }

    public void CloseDisplay(nint display)
    {
        Record(new CloseDisplayCall(display));
    }

    public nint DefaultRootWindow(nint display)
    {
        Record(new DefaultRootWindowCall(display));
        return RootWindow;
    }

    public ulong InternAtom(nint display, string name)
    {
        if (Record(new InternAtomCall(name)) is false)
        {
            return X11OverlayNative.AtomNone;
        }

        return Atoms.TryGetValue(name, out var atom) ? atom : X11OverlayNative.AtomNone;
    }

    public IReadOnlyList<uint>? ReadAtomProperty(nint display, nint window, ulong property, ulong type)
    {
        Record(new ReadAtomPropertyCall(window, property, type));

        if (ReadFails)
        {
            return null;
        }

        // A window the fake has no state staged for carries none: an empty list, exactly what
        // the server answers for a property that is not there.
        return StateAtoms.TryGetValue(window, out var atoms)
            ? atoms.Select(atom => (uint)atom).ToArray()
            : [];
    }

    public void ReplaceAtomProperty(nint display, nint window, ulong property, ulong type, ulong[] atoms)
    {
        Record(new ReplaceAtomPropertyCall(window, property, type, atoms));

        // The fake is the server now: what was written is what the next read would answer, so
        // a second application merges into the merged list rather than the original one.
        StateAtoms[window] = [.. atoms];
    }

    public void SendClientMessage(
        nint display,
        nint recipient,
        nint window,
        ulong messageType,
        long[] data,
        long eventMask)
    {
        Record(new SendClientMessageCall(recipient, window, messageType, data, eventMask));
    }

    public void RaiseWindow(nint display, nint window)
    {
        Record(new RaiseWindowCall(window));
    }

    public void Flush(nint display)
    {
        Record(new FlushCall());
    }

    /// <summary>
    /// Records one call and reports whether it should answer normally: a call armed with
    /// <see cref="ThrowOnNext"/> throws instead of answering, exactly once, the way a missing
    /// library fails its first P/Invoke and never reaches the rest.
    /// </summary>
    private bool Record<T>(T call) where T : X11OverlayCall
    {
        lock (_sync)
        {
            _calls.Add(call);
        }

        if (ThrowOnNext is not { } thrown)
        {
            return true;
        }

        // The records are named after the calls they stand for, with a Call suffix: the arming
        // name is the interface method, so the suffix comes off before the comparison.
        var method = typeof(T).Name;
        if (method.EndsWith("Call", StringComparison.Ordinal))
        {
            method = method[..^"Call".Length];
        }

        if (ThrowOnNextMethod is { } armed && armed != method)
        {
            return true;
        }

        ThrowOnNext = null;
        ThrowOnNextMethod = null;
        throw thrown;
    }
}
