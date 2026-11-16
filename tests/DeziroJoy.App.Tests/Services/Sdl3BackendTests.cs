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

    [Fact]
    public void The_Default_Mapping_Source_Is_The_Config_Root_File()
    {
        var source = Sdl3Backend.MappingSource();

        var file = Assert.IsType<Sdl3MappingFileSource>(source);
        Assert.Equal(Sdl3MappingFile.DefaultPath, file.Path);
        Assert.EndsWith("sdl-mappings.json", file.Path);
    }

    [Fact]
    public void The_Daemon_Provider_Loads_The_Persisted_Mappings_Before_Enumerating()
    {
        var guid = "03000000790000000006000000000000";
        var entry = new Sdl3MappingEntry
        {
            GuidString = guid,
            DeviceName = "DInput 手柄",
            Mapping = $"{guid},DInput 手柄,platform:Linux,a:b0",
            UpdatedUtc = "2026-11-16T00:00:00.0000000Z",
        };

        var previousSource = Sdl3Backend.MappingSource;
        var previousScan = Sdl3Backend.DiagnosticScan;
        Sdl3Backend.MappingSource = () => new InMemoryMappingSource([entry], null);
        // The real scan path: the provider's own first enumeration, read back as its diagnostic.
        Sdl3Backend.DiagnosticScan = static provider =>
        {
            provider.GetConnected();
            return provider.LastScanDiagnostic;
        };

        try
        {
            using var created = Sdl3Backend.CreateProvider();

            var provider = Assert.IsType<Sdl3GamepadProvider>(created);
            var diagnostic = provider.LastScanDiagnostic;
            Assert.NotNull(diagnostic);

            if (diagnostic!.Outcome == Sdl3ScanOutcome.SdlUnavailable)
            {
                // No native SDL3 runtime on this machine: the mapping path cannot be exercised,
                // and the failure is reported rather than hidden.
                Assert.Contains("SDL3", diagnostic.InitError ?? string.Empty);
                return;
            }

            Assert.Equal(1, diagnostic.AppliedMappingCount);
            Assert.True(diagnostic.HasAppliedMappings);
        }
        finally
        {
            Sdl3Backend.MappingSource = previousSource;
            Sdl3Backend.DiagnosticScan = previousScan;
        }
    }

    [Fact]
    public void A_Mapping_File_That_Cannot_Be_Read_Reaches_The_Daemon_Summary()
    {
        var previousSource = Sdl3Backend.MappingSource;
        var previousScan = Sdl3Backend.DiagnosticScan;
        Sdl3Backend.MappingSource = () => new InMemoryMappingSource([], "SDL 映射文件无法解析，已只使用 SDL 内置映射库：意外字符");
        Sdl3Backend.DiagnosticScan = static provider =>
        {
            provider.GetConnected();
            return provider.LastScanDiagnostic;
        };

        try
        {
            using var created = Sdl3Backend.CreateProvider();

            var provider = Assert.IsType<Sdl3GamepadProvider>(created);
            var diagnostic = provider.LastScanDiagnostic;

            if (diagnostic is { Outcome: Sdl3ScanOutcome.SdlUnavailable })
            {
                Assert.Contains("SDL3", diagnostic.InitError ?? string.Empty);
                return;
            }

            Assert.NotNull(diagnostic);
            Assert.Equal(0, diagnostic!.AppliedMappingCount);
            Assert.Contains("映射文件", diagnostic.MappingLoadError);
            Assert.Contains("sdl-mappings.json", diagnostic.Guidance);
        }
        finally
        {
            Sdl3Backend.MappingSource = previousSource;
            Sdl3Backend.DiagnosticScan = previousScan;
        }
    }

    private sealed class InMemoryMappingSource : ISdl3MappingSource
    {
        private readonly IReadOnlyList<Sdl3MappingEntry> _entries;
        private readonly string? _problem;

        public InMemoryMappingSource(IReadOnlyList<Sdl3MappingEntry> entries, string? problem)
        {
            _entries = entries;
            _problem = problem;
        }

        public Sdl3MappingSnapshot Load() => new(_entries, _problem);
    }
}
