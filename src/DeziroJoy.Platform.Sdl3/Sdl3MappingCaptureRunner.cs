namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// Where the operator's commands come from during a capture. The only question the runner asks
/// is "did they type something", and the answer must never block: a runner that waits for input
/// cannot keep sampling the device, and a redirected stdin would wedge the flow forever.
/// </summary>
public interface ISdl3MappingCaptureInput
{
    /// <summary>
    /// The next command character, or null when nothing is waiting. Commands the runner does not
    /// recognize are the implementation's to ignore or surface.
    /// </summary>
    char? TryReadCommand();
}

/// <summary>What one run of the capture loop produced.</summary>
/// <param name="Completed">True when every step was assigned or skipped.</param>
/// <param name="Cancelled">True when the operator aborted; nothing should then be saved.</param>
/// <param name="CapturedCount">Steps that ended with a real control.</param>
/// <param name="SkippedCount">Steps the operator marked as not provided by the device.</param>
public sealed record Sdl3MappingCaptureOutcome(
    bool Completed,
    bool Cancelled,
    int CapturedCount,
    int SkippedCount);

/// <summary>
/// Drives one <see cref="Sdl3MappingCaptureSession"/> against a live joystick: it takes a
/// baseline when each step starts, samples until a control activates, and answers the operator's
/// skip and cancel commands in between. The reader and the input are both seams, so the loop -
/// including "a button held across two steps is not captured twice" and "q abandons the whole
/// flow" - is exercised against scripted data instead of a controller.
/// </summary>
public static class Sdl3MappingCaptureRunner
{
    /// <summary>The skip command: this control is not on the device.</summary>
    public const char SkipCommand = 's';

    /// <summary>The cancel command: abandon the flow and save nothing.</summary>
    public const char CancelCommand = 'q';

    public static Sdl3MappingCaptureOutcome Run(
        Sdl3MappingCaptureSession session,
        ISdl3JoystickReader reader,
        ISdl3MappingCaptureInput input,
        TimeSpan pollInterval)
    {
        var captured = 0;
        var skipped = 0;

        while (!session.IsComplete && !session.IsCancelled)
        {
            var step = session.Current;
            if (step is null)
            {
                break;
            }

            session.BeginStep(reader.Sample());

            var settled = false;
            while (!settled && !session.IsCancelled)
            {
                if (session.TryCapture(reader.Sample()))
                {
                    captured++;
                    settled = true;
                    continue;
                }

                var command = input.TryReadCommand();
                if (command is CancelCommand or 'Q')
                {
                    session.Cancel();
                    break;
                }

                if (command is SkipCommand or 'S')
                {
                    session.Skip();
                    skipped++;
                    settled = true;
                    continue;
                }

                if (pollInterval > TimeSpan.Zero)
                {
                    Thread.Sleep(pollInterval);
                }
            }
        }

        return new Sdl3MappingCaptureOutcome(session.IsComplete, session.IsCancelled, captured, skipped);
    }
}
