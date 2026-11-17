using Avalonia;
using DeziroJoy.App;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The rendering-mode resolution and the Win32 options it produces. A launch that names
/// nothing gets the baseline its own entry point stands for; the --render= switch and the
/// DEZIROJOY_RENDER variable override that baseline for one run, the switch outranking the
/// variable; anything unreadable costs the override and nothing else; and off Windows the
/// whole switch is inert because the software mode exists on the Win32 backend alone.
/// <para>
/// Two baselines exist on purpose: the render window's is the persisted selection the user
/// chooses in settings, and the resident tray host's is a fixed software pin - a process that
/// never closes must not carry a GPU stack for the whole session on the user's behalf, and
/// must not start doing so because the user asked the window for GPU rendering.
/// </para>
/// </summary>
public sealed class AppRenderModeTests
{
    [Fact]
    public void Naming_Nothing_Keeps_The_Platform_Default()
    {
        var (mode, ignored) = AppRenderModes.Resolve(AppRenderMode.Default, null, null, isWindows: true);

        Assert.Equal(AppRenderMode.Default, mode);
        Assert.Null(ignored);
    }

    [Fact]
    public void The_Persisted_Selection_Is_What_A_Plain_Launch_Starts_With()
    {
        var (mode, ignored) = AppRenderModes.Resolve(AppRenderMode.Software, null, null, isWindows: true);

        Assert.Equal(AppRenderMode.Software, mode);
        Assert.Null(ignored);
    }

    [Fact]
    public void The_Resident_Host_Baseline_Is_A_Fixed_Software_Pin()
    {
        // The one GUI process that never closes: an icon and two small overlays are not worth
        // a GPU stack held for the whole logon session.
        Assert.Equal(AppRenderMode.Software, AppRenderModes.ResidentHostBaseline);

        var (mode, ignored) = AppRenderModes.Resolve(
            AppRenderModes.ResidentHostBaseline, null, null, isWindows: true);

        Assert.Equal(AppRenderMode.Software, mode);
        Assert.Null(ignored);
    }

    [Fact]
    public void The_Resident_Host_Never_Reads_The_Windows_Persisted_Selection()
    {
        // A user who picks GPU rendering for the main window must not hand the GPU stack back
        // to the resident host: the tray entry point passes its own baseline in and never the
        // persisted one, which is what this pins down - the same "Default" the window would
        // save resolves to software for the host.
        var (window, _) = AppRenderModes.Resolve(AppRenderMode.Default, null, null, isWindows: true);
        var (host, _) = AppRenderModes.Resolve(AppRenderModes.ResidentHostBaseline, null, null, isWindows: true);

        Assert.Equal(AppRenderMode.Default, window);
        Assert.Equal(AppRenderMode.Software, host);
    }

    [Fact]
    public void An_Explicit_Override_Still_Moves_The_Resident_Host_For_Diagnostics()
    {
        // Comparing the two renderers on the tray surfaces is what the variable is for there;
        // it is the only thing that can move the pin, and it moves it per run, not on disk.
        var (mode, ignored) = AppRenderModes.Resolve(
            AppRenderModes.ResidentHostBaseline, null, AppRenderModes.DefaultName, isWindows: true);

        Assert.Equal(AppRenderMode.Default, mode);
        Assert.Null(ignored);
    }

    [Fact]
    public void Off_Windows_The_Resident_Host_Pin_Is_Inert_Too()
    {
        var (mode, ignored) = AppRenderModes.Resolve(
            AppRenderModes.ResidentHostBaseline, null, null, isWindows: false);

        Assert.Equal(AppRenderMode.Default, mode);
        Assert.Null(ignored);
    }

    [Fact]
    public void The_Environment_Variable_Overrides_The_Persisted_Selection()
    {
        var (software, _) = AppRenderModes.Resolve(AppRenderMode.Default, null, "software", isWindows: true);
        Assert.Equal(AppRenderMode.Software, software);

        var (back, _) = AppRenderModes.Resolve(AppRenderMode.Software, null, "default", isWindows: true);
        Assert.Equal(AppRenderMode.Default, back);
    }

    [Fact]
    public void The_Command_Line_Switch_Outranks_The_Environment_Variable()
    {
        var (software, _) = AppRenderModes.Resolve(AppRenderMode.Default, "software", "default", isWindows: true);
        Assert.Equal(AppRenderMode.Software, software);

        var (back, _) = AppRenderModes.Resolve(AppRenderMode.Default, "default", "software", isWindows: true);
        Assert.Equal(AppRenderMode.Default, back);
    }

    [Fact]
    public void An_Override_That_Cannot_Be_Parsed_Is_Ignored_With_The_Reason()
    {
        var (mode, ignored) = AppRenderModes.Resolve(AppRenderMode.Software, null, "vulkan", isWindows: true);

        // The persisted selection survives the bad override, and the run is told why the
        // override did not take.
        Assert.Equal(AppRenderMode.Software, mode);
        Assert.NotNull(ignored);
        Assert.Contains(AppRenderModes.EnvironmentVariable, ignored);
        Assert.Contains("vulkan", ignored);
    }

    [Fact]
    public void A_Damaged_Override_Falls_Through_To_The_Next_Source()
    {
        // The switch names garbage and the variable names a real mode: the variable decides,
        // and the ignored switch is still reported instead of vanishing.
        var (mode, ignored) = AppRenderModes.Resolve(AppRenderMode.Default, "banana", "software", isWindows: true);

        Assert.Equal(AppRenderMode.Software, mode);
        Assert.NotNull(ignored);
        Assert.Contains(AppRenderModes.CommandLineSwitch, ignored);
    }

    [Fact]
    public void Off_Windows_The_Platform_Default_Runs_No_Matter_What_Either_Source_Says()
    {
        var (fromPersisted, persistedNote) = AppRenderModes.Resolve(AppRenderMode.Software, null, null, isWindows: false);
        Assert.Equal(AppRenderMode.Default, fromPersisted);
        Assert.Null(persistedNote);

        var (fromVariable, variableNote) = AppRenderModes.Resolve(AppRenderMode.Default, null, "software", isWindows: false);
        Assert.Equal(AppRenderMode.Default, fromVariable);
        Assert.NotNull(variableNote);
        Assert.Contains("仅 Windows 有效", variableNote);

        var (fromSwitch, switchNote) = AppRenderModes.Resolve(AppRenderMode.Default, "software", null, isWindows: false);
        Assert.Equal(AppRenderMode.Default, fromSwitch);
        Assert.NotNull(switchNote);
        Assert.Contains("仅 Windows 有效", switchNote);
    }

    [Fact]
    public void Names_Round_Trip_And_Unknown_Names_Degrade()
    {
        Assert.Equal("software", AppRenderModes.ToName(AppRenderMode.Software));
        Assert.Equal("default", AppRenderModes.ToName(AppRenderMode.Default));

        Assert.Equal(AppRenderMode.Software, AppRenderModes.Parse("SOFTWARE").Mode);
        Assert.Equal(AppRenderMode.Default, AppRenderModes.Parse("Default").Mode);
        Assert.Null(AppRenderModes.Parse(null).Problem);
        Assert.Null(AppRenderModes.Parse("  ").Problem);

        var (mode, problem) = AppRenderModes.Parse("metal");
        Assert.Equal(AppRenderMode.Default, mode);
        Assert.NotNull(problem);
        Assert.Contains("metal", problem);
    }

    [Fact]
    public void Software_Asks_For_The_Win32_Software_Backend_And_Nothing_Else()
    {
        var options = AppRenderModes.Win32Options(AppRenderMode.Software);

        Assert.NotNull(options);

        // Software is the only requested mode: keeping AngleEgl ahead of it would be exactly
        // what the platform default already means, and an empty fallback chain is what makes
        // the switch observable at all.
        Assert.Equal(new[] { Win32RenderingMode.Software }, options.RenderingMode);
        Assert.DoesNotContain(Win32RenderingMode.AngleEgl, options.RenderingMode);
    }

    [Fact]
    public void Default_Registers_No_Win32_Options_At_All()
    {
        // Null means the builder chain is left untouched, so Avalonia applies its own
        // platform detection - the documented default everywhere.
        Assert.Null(AppRenderModes.Win32Options(AppRenderMode.Default));
    }

    [Fact]
    public void Both_GUI_Entries_Build_Without_Initializing_A_Renderer()
    {
        // Constructing a builder only resolves the policy and registers options - no platform
        // is initialized - so this runs everywhere and proves each entry point is wired to its
        // own baseline: the render window reads the persisted selection, the resident host
        // passes its fixed software pin, and neither throws while doing it.
        Assert.NotNull(Program.BuildAvaloniaApp());
        Assert.NotNull(Program.BuildTrayAvaloniaApp());
    }
}
