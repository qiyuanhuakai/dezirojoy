using DeziroJoy.App.Services;
using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Sdl3;
using DeziroJoy.Platform.Windows.XInput;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The backend-aware provider selection. The platform backend stays the default; SDL3 is
/// opt-in; the provider-layer virtual pad keeps priority over both; and a requested SDL3
/// that cannot start is reported instead of silently falling back to the platform backend.
/// </summary>
public class PlatformFactoryBackendTests
{
    [Fact]
    public void The_Platform_Backend_Is_The_Default()
    {
        Assert.True(PlatformFactory.TryCreateProvider(out var provider));

        AssertPlatformProvider(provider);
    }

    [Fact]
    public void The_Explicit_Platform_Selection_Matches_The_Default()
    {
        var created = PlatformFactory.TryCreateProvider(InputBackend.Platform, out var provider, out var error);

        Assert.True(created);
        Assert.Null(error);
        AssertPlatformProvider(provider);
    }

    [Fact]
    public void The_Virtual_Pad_Keeps_Priority_Over_Every_Backend()
    {
        var probe = new FakeGamepadProvider();
        var previous = PlatformFactory.VirtualProviderProbe;
        PlatformFactory.VirtualProviderProbe = () => probe;
        try
        {
            Assert.True(PlatformFactory.TryCreateProvider(InputBackend.Sdl3, out var sdl3Selection, out var sdl3Error));
            Assert.Same(probe, sdl3Selection);
            Assert.Null(sdl3Error);

            Assert.True(PlatformFactory.TryCreateProvider(InputBackend.Platform, out var platformSelection, out _));
            Assert.Same(probe, platformSelection);
        }
        finally
        {
            PlatformFactory.VirtualProviderProbe = previous;
        }
    }

    [Fact]
    public void The_Sdl3_Selection_Never_Falls_Back_To_The_Platform_Backend()
    {
        var created = PlatformFactory.TryCreateProvider(InputBackend.Sdl3, out var provider, out var error);
        try
        {
            if (created)
            {
                Assert.IsType<Sdl3GamepadProvider>(provider);
                Assert.Null(error);
            }
            else
            {
                // The failure is diagnosable: the reason travels back, and no provider was
                // substituted for the one that was asked for.
                Assert.Null(provider);
                Assert.Contains("SDL3", error);
            }

            AssertPlatformProviderWasNotSubstituted(provider);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void The_Backend_Description_Names_The_Opt_In_Status()
    {
        Assert.Contains("SDL3", PlatformFactory.DescribeBackend(InputBackend.Sdl3));
        Assert.Contains("实验性", PlatformFactory.DescribeBackend(InputBackend.Sdl3));
        Assert.Contains("平台默认", PlatformFactory.DescribeBackend(InputBackend.Platform));
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
