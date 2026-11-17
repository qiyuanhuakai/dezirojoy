using DeziroJoy.App.Services;
using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Sdl3;
using DeziroJoy.Platform.Windows.XInput;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The S3 default switch, end to end from the settings file the daemon reads: a new install
/// and an upgrade that never chose both land on SDL3, an explicit legacy choice - including
/// the <c>platform</c> name alpha.1–alpha.4 wrote - stays on the legacy backend, and a
/// damaged file costs the selection and nothing else. Each scenario runs the real store into
/// the real composition, so what is pinned is the backend the daemon would actually build
/// and the line its journal would print, not an intermediate value.
/// </summary>
public class InputBackendUpgradeTests
{
    [Fact]
    public void A_New_Install_Without_A_Selection_Defaults_To_Sdl3()
    {
        var path = FreshPath();

        using var composition = ComposeFrom(path);

        Assert.Equal(InputBackend.Sdl3, composition.Backend);
        AssertSdl3ProviderOrDiagnosableFailure(composition.Provider, composition.ProviderError);
        Assert.Contains("input backend: SDL3 统一后端（默认）", Summary(composition));
    }

    [Fact]
    public void An_Upgrade_That_Never_Chose_Also_Defaults_To_Sdl3()
    {
        // The upgrade case RoadMap's S3 names: the file only exists once a user saves a
        // selection, so its absence is the "未选择" state - and that state moves to the new
        // default rather than staying on the backend the older build ran by default.
        var path = FreshPath();

        var backend = new InputBackendStore(path).Load();

        Assert.Equal(InputBackend.Sdl3, backend);
    }

    [Fact]
    public void An_Upgrade_That_Explicitly_Chose_The_Platform_Backend_Stays_On_Legacy()
    {
        // alpha.1–alpha.4 wrote "platform" when the user picked 平台默认. The option was
        // renamed, not the choice: the explicit legacy selection survives the upgrade.
        var path = FreshPath();
        File.WriteAllText(path, """{"backend":"platform"}""");

        using var composition = ComposeFrom(path);

        Assert.Equal(InputBackend.Legacy, composition.Backend);
        Assert.Null(composition.ProviderError);
        AssertPlatformProvider(composition.Provider);
        Assert.Contains("input backend: 旧后端回退", Summary(composition));
    }

    [Fact]
    public void An_Explicit_Legacy_Selection_Under_The_Current_Name_Stays_On_Legacy()
    {
        var path = FreshPath();
        new InputBackendStore(path).Save(InputBackend.Legacy);

        using var composition = ComposeFrom(path);

        Assert.Equal(InputBackend.Legacy, composition.Backend);
        AssertPlatformProvider(composition.Provider);
    }

    [Fact]
    public void An_Explicit_Sdl3_Selection_From_An_Earlier_Alpha_Stays_On_Sdl3()
    {
        var path = FreshPath();
        File.WriteAllText(path, """{"backend":"sdl3"}""");

        using var composition = ComposeFrom(path);

        Assert.Equal(InputBackend.Sdl3, composition.Backend);
        AssertSdl3ProviderOrDiagnosableFailure(composition.Provider, composition.ProviderError);
    }

    [Fact]
    public void A_Damaged_File_Defaults_To_Sdl3_With_The_Reason_In_The_Journal()
    {
        var path = FreshPath();
        File.WriteAllText(path, "this is not json");

        var store = new InputBackendStore(path);
        var backend = store.Load();

        Assert.Equal(InputBackend.Sdl3, backend);
        Assert.Contains("无法解析", store.LastError);

        using var composition = new EngineComposition(backend: backend);
        Assert.Contains("input backend: SDL3 统一后端（默认）", Summary(composition));
    }

    [Fact]
    public void An_Unrecognized_Name_Defaults_To_Sdl3_With_The_Reason()
    {
        var path = FreshPath();
        File.WriteAllText(path, """{"backend":"gameinput"}""");

        var store = new InputBackendStore(path);

        Assert.Equal(InputBackend.Sdl3, store.Load());
        Assert.Contains("无法识别", store.LastError);
    }

    private static EngineComposition ComposeFrom(string path)
    {
        var store = new InputBackendStore(path);
        return new EngineComposition(backend: store.Load());
    }

    private static string Summary(EngineComposition composition)
        => DaemonHost.BuildStartupSummary(composition, TimeSpan.FromMilliseconds(2));

    private static string FreshPath()
        => Path.Combine(Path.GetTempPath(), $"dezirojoy-backend-{Guid.NewGuid():N}.json");

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
}
