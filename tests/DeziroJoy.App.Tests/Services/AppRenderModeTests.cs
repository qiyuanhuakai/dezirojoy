using Avalonia;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The rendering-mode resolution and the Win32 options it produces. A launch that names
/// nothing gets the platform default; the --render= switch and the DEZIROJOY_RENDER variable
/// override the persisted selection for one run, the switch outranking the variable; anything
/// unreadable costs the override and nothing else; and off Windows the whole switch is inert
/// because the software mode exists on the Win32 backend alone.
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
}
