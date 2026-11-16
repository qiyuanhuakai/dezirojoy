using DeziroJoy.App.Services;
using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Sdl3;
using DeziroJoy.Platform.Windows.XInput;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The backend selection as the daemon and GUI compose it: the platform default stays the
/// default, an SDL3 selection reaches the provider without a silent fallback, and the
/// startup summary names the backend - with the reason a failed one could not start - so a
/// headless install's journal is the diagnosis.
/// </summary>
public class InputBackendCompositionTests
{
    [Fact]
    public void The_Default_Composition_Runs_The_Platform_Backend()
    {
        using var composition = new EngineComposition();

        Assert.Equal(InputBackend.Platform, composition.Backend);
        Assert.Null(composition.ProviderError);
        AssertPlatformProvider(composition.Provider);
    }

    [Fact]
    public void An_Sdl3_Selection_Reaches_The_Sdl3_Provider_Or_Fails_Diagnosably()
    {
        using var composition = new EngineComposition(backend: InputBackend.Sdl3);

        Assert.Equal(InputBackend.Sdl3, composition.Backend);

        if (composition.ProviderError is null)
        {
            Assert.IsType<Sdl3GamepadProvider>(composition.Provider);
        }
        else
        {
            // The native runtime is missing on this machine: the selection failed with the
            // reason and no provider was substituted for the one that was asked for.
            Assert.Contains("SDL3", composition.ProviderError);
            Assert.IsType<NoGamepadProvider>(composition.Provider);
        }

        AssertPlatformProviderWasNotSubstituted(composition.Provider);
    }

    [Fact]
    public void The_Startup_Summary_Names_The_Selected_Backend()
    {
        using var platform = new EngineComposition();
        var platformSummary = DaemonHost.BuildStartupSummary(platform, TimeSpan.FromMilliseconds(2));
        Assert.Contains("input backend: 平台默认", platformSummary);
        Assert.DoesNotContain("input backend error", platformSummary);

        using var sdl3 = new EngineComposition(backend: InputBackend.Sdl3);
        var sdl3Summary = DaemonHost.BuildStartupSummary(sdl3, TimeSpan.FromMilliseconds(2));
        Assert.Contains("input backend: SDL3", sdl3Summary);
        Assert.Contains("engine started", sdl3Summary);
    }

    [Fact]
    public void The_Device_Note_Quotes_The_Sdl3_Guidance_For_A_Zero_Count()
    {
        var diagnostic = new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.SdlUnavailable,
            Runtime = new Sdl3RuntimeInfo(30420, "abc123"),
            InitError = "Failed to load SDL3",
            GamepadCount = 0,
        };

        var note = DaemonHost.BuildDeviceNote(diagnostic, controllerCount: 0);

        Assert.NotNull(note);
        Assert.Contains("SDL3", note);
        Assert.Contains("Failed to load SDL3", note);
    }

    [Fact]
    public void The_Device_Note_Stays_Silent_For_A_Healthy_Pad()
    {
        var diagnostic = new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.GamepadsFound,
            GamepadCount = 1,
        };

        Assert.Null(DaemonHost.BuildDeviceNote(diagnostic, controllerCount: 1));
    }

    [Fact]
    public void The_Device_Note_Does_Not_Contradict_The_Devices_In_View()
    {
        // A stale scan still reading "nothing recognized" beside a connected pad says
        // nothing, exactly as the evdev note does: the pad in view is the truth.
        var stale = new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.NoGamepads,
            GamepadCount = 0,
        };

        Assert.Null(DaemonHost.BuildDeviceNote(stale, controllerCount: 1));
    }

    [Fact]
    public void The_Device_Note_Quotes_The_Unstable_Identity_Warning_Beside_A_Working_Pad()
    {
        var diagnostic = new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.GamepadsFound,
            GamepadCount = 1,
            UnstableIdentityInstanceIds = [7],
        };

        var note = DaemonHost.BuildDeviceNote(diagnostic, controllerCount: 1);

        Assert.NotNull(note);
        Assert.Contains("#7", note);
    }

    [Fact]
    public void The_Device_Note_Is_Silent_Without_A_Scan()
    {
        Assert.Null(DaemonHost.BuildDeviceNote((Sdl3ScanDiagnostic?)null, controllerCount: 0));
    }

    private static void AssertPlatformProvider(IGamepadProvider provider)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<XInputGamepadProvider>(provider);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.IsType<EvdevGamepadProvider>(provider);
        }
        else
        {
            Assert.IsType<NoGamepadProvider>(provider);
        }
    }

    private static void AssertPlatformProviderWasNotSubstituted(IGamepadProvider? provider)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.IsNotType<XInputGamepadProvider>(provider);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.IsNotType<EvdevGamepadProvider>(provider);
        }
    }
}
