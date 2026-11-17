using DeziroJoy.App.Services;
using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Sdl3;
using DeziroJoy.Platform.Windows.XInput;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The backend-aware provider selection. SDL3 is the default since S3, the legacy backend is
/// the explicit fallback, the provider-layer virtual pad keeps priority over both - one
/// provider per pad, never two - and a requested backend that cannot start is reported
/// instead of silently reading through the other one.
/// </summary>
public class PlatformFactoryBackendTests
{
    [Fact]
    public void The_Default_Backend_Is_Sdl3()
    {
        var previousProbe = PlatformFactory.VirtualProviderProbe;
        PlatformFactory.VirtualProviderProbe = static () => null;
        try
        {
            var created = PlatformFactory.TryCreateProvider(out var provider);
            try
            {
                if (created)
                {
                    Assert.IsType<Sdl3GamepadProvider>(provider);
                }
                else
                {
                    // The native runtime is missing on this machine: the default failed with no
                    // provider substituted, exactly as an explicit selection would.
                    Assert.Null(provider);
                }
            }
            finally
            {
                (provider as IDisposable)?.Dispose();
            }
        }
        finally
        {
            PlatformFactory.VirtualProviderProbe = previousProbe;
        }
    }

    [Fact]
    public void The_Explicit_Legacy_Selection_Reaches_The_Platform_Provider()
    {
        var created = PlatformFactory.TryCreateProvider(InputBackend.Legacy, out var provider, out var error);

        Assert.True(created);
        Assert.Null(error);
        AssertPlatformProvider(provider);
        (provider as IDisposable)?.Dispose();
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

            Assert.True(PlatformFactory.TryCreateProvider(InputBackend.Legacy, out var legacySelection, out _));
            Assert.Same(probe, legacySelection);

            Assert.True(PlatformFactory.TryCreateProvider(out var defaultSelection));
            Assert.Same(probe, defaultSelection);
        }
        finally
        {
            PlatformFactory.VirtualProviderProbe = previous;
        }
    }

    [Fact]
    public void The_Sdl3_Selection_Never_Falls_Back_To_The_Legacy_Backend()
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
    public void The_Backend_Description_Names_The_Default_And_The_Fallback()
    {
        Assert.Contains("SDL3", PlatformFactory.DescribeBackend(InputBackend.Sdl3));
        Assert.Contains("默认", PlatformFactory.DescribeBackend(InputBackend.Sdl3));
        Assert.Contains("回退", PlatformFactory.DescribeBackend(InputBackend.Legacy));

        // The fallback names the platform backend the user is falling back to.
        var legacy = PlatformFactory.DescribeBackend(InputBackend.Legacy);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("XInput", legacy);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Contains("evdev", legacy);
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
