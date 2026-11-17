using DeziroJoy.App.Services;
using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Sdl3;
using DeziroJoy.Platform.Windows.XInput;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The backend selection as the daemon and GUI compose it: SDL3 is the default since S3, an
/// explicit legacy selection reaches the platform provider, an SDL3 selection reaches the
/// provider without a silent fallback, and the startup summary names the backend - with the
/// reason a failed one could not start - so a headless install's journal is the diagnosis.
/// </summary>
public class InputBackendCompositionTests
{
    [Fact]
    public void The_Default_Composition_Runs_Sdl3()
    {
        using var composition = new EngineComposition();

        // The default is SDL3 whether or not this machine's native runtime came up: a
        // machine without it fails the default diagnosably rather than reading legacy pads.
        Assert.Equal(InputBackend.Sdl3, composition.Backend);
        AssertSdl3ProviderOrDiagnosableFailure(composition.Provider, composition.ProviderError);
    }

    [Fact]
    public void An_Explicit_Legacy_Selection_Reaches_The_Platform_Provider()
    {
        using var composition = new EngineComposition(backend: InputBackend.Legacy);

        Assert.Equal(InputBackend.Legacy, composition.Backend);
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
    public void A_Default_Sdl3_That_Cannot_Start_Never_Falls_Back_To_Legacy()
    {
        // The S3 default is not a silent switch: a machine whose SDL3 runtime will not come
        // up runs without a provider and says why, exactly as an explicit selection would -
        // the daemon's journal and the settings card carry the reason either way.
        var previous = Sdl3Backend.DiagnosticScan;
        var previousProbe = PlatformFactory.VirtualProviderProbe;
        Sdl3Backend.DiagnosticScan = _ => new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.SdlUnavailable,
            InitError = "找不到原生 SDL3 运行时（Linux: libSDL3.so）",
            GamepadCount = 0,
        };
        PlatformFactory.VirtualProviderProbe = static () => null;

        try
        {
            using var composition = new EngineComposition();

            Assert.Equal(InputBackend.Sdl3, composition.Backend);
            Assert.Contains("SDL3", composition.ProviderError!);
            Assert.Contains("libSDL3.so", composition.ProviderError!);
            Assert.IsType<NoGamepadProvider>(composition.Provider);
            AssertPlatformProviderWasNotSubstituted(composition.Provider);

            var summary = DaemonHost.BuildStartupSummary(composition, TimeSpan.FromMilliseconds(2));
            Assert.Contains("input backend: SDL3", summary);
            Assert.Contains("input backend error", summary);
            Assert.Contains("未切换回其他后端", summary);
        }
        finally
        {
            Sdl3Backend.DiagnosticScan = previous;
            PlatformFactory.VirtualProviderProbe = previousProbe;
        }
    }

    [Fact]
    public void The_Startup_Summary_Names_The_Selected_Backend()
    {
        using var legacy = new EngineComposition(backend: InputBackend.Legacy);
        var legacySummary = DaemonHost.BuildStartupSummary(legacy, TimeSpan.FromMilliseconds(2));
        Assert.Contains("input backend: 旧后端回退", legacySummary);
        Assert.DoesNotContain("input backend error", legacySummary);

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

    private static void AssertSdl3ProviderOrDiagnosableFailure(IGamepadProvider provider, string? error)
    {
        if (error is null)
        {
            Assert.IsType<Sdl3GamepadProvider>(provider);
        }
        else
        {
            Assert.Contains("SDL3", error);
            Assert.IsType<NoGamepadProvider>(provider);
        }
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
