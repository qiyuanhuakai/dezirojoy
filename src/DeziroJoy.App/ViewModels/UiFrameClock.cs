namespace DeziroJoy.App.ViewModels;

/// <summary>
/// A UI frame clock: raises <see cref="Tick"/> on the UI thread once per frame while it is
/// ticking. The coalesced input-sample dispatch rides one, so a sample stream that arrives
/// faster than the display refreshes costs one UI update per frame instead of one queued
/// update per sample.
/// <para>
/// The interface is deliberately Avalonia-free: the view models that consume it stay
/// unit-testable without a dispatcher. Production runs a 16 ms DispatcherTimer
/// (<see cref="Services.DispatcherFrameClock"/>); a test drives the tick by hand.
/// </para>
/// </summary>
public interface IUiFrameClock : IDisposable
{
    /// <summary>Raised on the UI thread once per frame while the clock is ticking.</summary>
    event Action? Tick;

    /// <summary>Starts the frame clock. Calling it again while it already ticks is a no-op.</summary>
    void StartTicking();

    /// <summary>Stops the frame clock. Calling it again while it is already stopped is a no-op.</summary>
    void StopTicking();
}
