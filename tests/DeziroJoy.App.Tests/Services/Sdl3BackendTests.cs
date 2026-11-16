using DeziroJoy.App.Services;
using DeziroJoy.Platform.Sdl3;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The SDL3 backend seam. Construction alone proves nothing - the provider initializes
/// lazily - so the factory runs the minimal scan and only accepts a runtime that actually
/// came up: unavailable fails the selection with the reason (and no provider substituted),
/// while a runtime with no gamepads is a working backend, not an error.
/// </summary>
public class Sdl3BackendTests
{
    [Fact]
    public void An_Unavailable_Runtime_Fails_The_Selection_With_The_Reason_And_Disposes()
    {
        Sdl3GamepadProvider? scanned = null;
        var previous = Sdl3Backend.DiagnosticScan;
        Sdl3Backend.DiagnosticScan = provider =>
        {
            scanned = provider;
            return new Sdl3ScanDiagnostic
            {
                Outcome = Sdl3ScanOutcome.SdlUnavailable,
                InitError = "找不到原生 SDL3 运行时（Windows: SDL3.dll，Linux: libSDL3.so）",
                GamepadCount = 0,
            };
        };

        try
        {
            var failure = Assert.Throws<InvalidOperationException>(Sdl3Backend.CreateProvider);

            Assert.Contains("SDL3", failure.Message);
            Assert.Contains("找不到原生 SDL3 运行时", failure.Message);

            // The half-started provider is released, not leaked: after the failed creation
            // it reports no devices and polls nothing.
            Assert.NotNull(scanned);
            Assert.Empty(scanned!.GetConnected());
            Assert.Null(scanned.Poll("any-device"));
        }
        finally
        {
            Sdl3Backend.DiagnosticScan = previous;
        }
    }

    [Fact]
    public void A_Runtime_Without_Gamepads_Is_Not_A_Failure()
    {
        var previous = Sdl3Backend.DiagnosticScan;
        Sdl3Backend.DiagnosticScan = _ => new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.NoGamepads,
            GamepadCount = 0,
        };

        try
        {
            using var provider = Sdl3Backend.CreateProvider();

            // Nothing plugged in is a working backend: the guidance about it reaches the
            // user through the daemon's device note, not as a failed selection.
            Assert.IsType<Sdl3GamepadProvider>(provider);
        }
        finally
        {
            Sdl3Backend.DiagnosticScan = previous;
        }
    }

    [Fact]
    public void A_Runtime_With_Gamepads_Passes_Through()
    {
        var previous = Sdl3Backend.DiagnosticScan;
        Sdl3Backend.DiagnosticScan = _ => new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.GamepadsFound,
            GamepadCount = 1,
        };

        try
        {
            using var provider = Sdl3Backend.CreateProvider();

            Assert.IsType<Sdl3GamepadProvider>(provider);
        }
        finally
        {
            Sdl3Backend.DiagnosticScan = previous;
        }
    }

    [Fact]
    public void The_Real_Runtime_Either_Starts_Or_Fails_Diagnosably()
    {
        // The native library ships with the build, so this is the real path: a provider on a
        // machine that has the runtime (with or without pads), a diagnosable failure on one
        // that does not - never a silent success.
        try
        {
            using var provider = Sdl3Backend.CreateProvider();
            Assert.IsType<Sdl3GamepadProvider>(provider);
        }
        catch (InvalidOperationException exception)
        {
            Assert.Contains("SDL3", exception.Message);
        }
    }
}
