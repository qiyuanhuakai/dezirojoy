using Avalonia.Threading;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Services;

/// <summary>
/// The production frame clock: a 16 ms <see cref="DispatcherTimer"/>, the same cadence the
/// render window's preview tick runs at, so one coalesced sample dispatch lands per displayed
/// frame. Start and stop are safe from any thread - the timer itself is only ever touched on
/// the UI thread - because the daemon's receive loop is what asks a clock to start.
/// </summary>
internal sealed class DispatcherFrameClock : IUiFrameClock
{
    /// <summary>
    /// One dispatch per displayed frame. Matches the window's own preview interval, so a
    /// sample that lands mid-frame is dispatched by the same beat the preview animates on.
    /// </summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly DispatcherTimer _timer = new() { Interval = FrameInterval };

    public DispatcherFrameClock() => _timer.Tick += OnTimerTick;

    public event Action? Tick;

    public void StartTicking()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _timer.Start();
            return;
        }

        Dispatcher.UIThread.Post(_timer.Start);
    }

    public void StopTicking()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _timer.Stop();
            return;
        }

        Dispatcher.UIThread.Post(_timer.Stop);
    }

    public void Dispose()
    {
        StopTicking();
        _timer.Tick -= OnTimerTick;
    }

    private void OnTimerTick(object? sender, EventArgs e) => Tick?.Invoke();
}
