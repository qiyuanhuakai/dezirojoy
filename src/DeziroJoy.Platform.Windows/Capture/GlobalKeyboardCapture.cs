using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Platform.Windows.Capture;

/// <summary>
/// Arms a <see cref="KeyboardCaptureSession"/> behind an <see cref="ILowLevelKeyboardHook"/> for
/// exactly as long as the mapping editor is waiting for a key. It owns the hook's lifecycle:
/// idempotent arm/disarm, seeding the held modifiers at arm time, forwarding the captured combo,
/// and - the subtle part - never unhooking from inside the hook's own callback, because
/// <c>UnhookWindowsHookEx</c> called re-entrantly from a low-level callback can deadlock. When the
/// session ends it detaches its handler at once (so trailing events pass through) and defers the
/// actual unhook to <paramref name="post"/>, which production points at the UI thread.
/// <para>
/// The hook is injected rather than built here, so this type carries no native dependency and its
/// logic is exercised on any platform; the real <c>WH_KEYBOARD_LL</c> hook is supplied only by the
/// Windows-guarded factory in the app.
/// </para>
/// </summary>
public sealed class GlobalKeyboardCapture : IGlobalKeyboardCapture
{
    private readonly ILowLevelKeyboardHook _hook;
    private readonly Action<Action> _post;

    private KeyboardCaptureSession? _session;
    private bool _started;

    public GlobalKeyboardCapture(ILowLevelKeyboardHook hook, Action<Action>? post = null)
    {
        _hook = hook;

        // Production injects the Avalonia UI-thread post so the deferred unhook runs off the
        // callback's stack. The synchronous default is only for standalone use and tests, where
        // there is no real callback stack to escape.
        _post = post ?? (static action => action());
    }

    public event Action<GamepadKey, KeyModifiers>? KeyCaptured;

    public event Action? CaptureEnded;

    public void Arm()
    {
        if (_started)
        {
            return;
        }

        _session = new KeyboardCaptureSession(_hook.ReadHeldModifiers());
        _hook.Handler = OnKeyEvent;

        if (!_hook.Install())
        {
            // The hook refused to install. Drop the handler and end the capture rather than
            // leaving the editor waiting for a key that will never arrive.
            _hook.Handler = null;
            _session = null;
            CaptureEnded?.Invoke();
            return;
        }

        _started = true;
    }

    public void Disarm()
    {
        if (!_started)
        {
            return;
        }

        _session = null;
        _hook.Handler = null;
        _hook.Uninstall();
        _started = false;
    }

    public void Dispose() => Disarm();

    private bool OnKeyEvent(RawKeyEvent e)
    {
        try
        {
            var session = _session;
            if (session is null)
            {
                return false;
            }

            var step = session.Process(e);

            if (step.CapturedKey is { } key)
            {
                try
                {
                    KeyCaptured?.Invoke(key, step.CapturedModifiers);
                }
                catch
                {
                    // One bad subscriber must not stop the capture or wedge the swallow decision.
                }
            }

            if (step.Ended)
            {
                EndCapture();
            }

            return step.Decision == CaptureDecision.Swallow;
        }
        catch
        {
            // Session processing threw unexpectedly: stop swallowing, end the capture, and let
            // the deferred unhook run from a safe point rather than from inside the callback.
            EndCapture();
            return false;
        }
    }

    /// <summary>
    /// Detaches the handler immediately (so no further event is swallowed) and schedules the real
    /// unhook off the callback stack. Safe to call more than once.
    /// </summary>
    private void EndCapture()
    {
        // Detach the handler and session at once so nothing further is swallowed, but leave the
        // hook installed: the queued Disarm removes it from off this callback's stack.
        _session = null;
        _hook.Handler = null;

        try
        {
            CaptureEnded?.Invoke();
        }
        catch
        {
            // The capture is over regardless of whether a subscriber could be told.
        }

        _post(Disarm);
    }
}
