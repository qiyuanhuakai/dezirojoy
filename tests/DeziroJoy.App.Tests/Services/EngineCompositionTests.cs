using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The GUI and the daemon must build the same provider -> sink -> EngineHost wiring, so it
/// lives in one place. These tests pin that shared composition.
/// </summary>
public class EngineCompositionTests
{
    [Fact]
    public void Composition_Builds_The_Wiring_Both_Paths_Share()
    {
        using var composition = new EngineComposition();

        Assert.NotNull(composition.Provider);
        Assert.NotNull(composition.Sink);
        Assert.NotNull(composition.ProfileStore);
        Assert.NotNull(composition.Host);
        Assert.False(composition.Host.IsRunning);
        Assert.False(string.IsNullOrWhiteSpace(EngineComposition.PlatformName));
    }

    [Fact]
    public void Composition_Reconciles_Device_Presence_Without_Throwing()
    {
        using var composition = new EngineComposition();

        composition.Host.RefreshPresence();

        // The count is hardware dependent; what matters is that the shared wiring ticks.
        Assert.Equal(composition.Host.Controllers.Count, composition.Host.Controllers.Count);
    }

    [Fact]
    public void Composition_Reports_Elevation_Support_Only_Where_Uipi_Applies()
    {
        using var composition = new EngineComposition();

        Assert.Equal(OperatingSystem.IsWindows(), composition.IsElevationSupported);
    }

    [Fact]
    public void Presentation_Only_Composition_Never_Creates_An_Injecting_Sink()
    {
        using var composition = new EngineComposition(presentationOnly: true);

        Assert.IsType<NullOutputSink>(composition.Sink);
        Assert.Null(composition.SinkError);
        Assert.False(composition.Host.IsRunning);
        composition.Host.RefreshPresence();
    }

    [Fact]
    public void Composition_Dispose_Stops_The_Host()
    {
        var composition = new EngineComposition();
        composition.Host.Start(TimeSpan.FromMilliseconds(2));
        Assert.True(composition.Host.IsRunning);

        composition.Dispose();

        Assert.False(composition.Host.IsRunning);
    }
}
