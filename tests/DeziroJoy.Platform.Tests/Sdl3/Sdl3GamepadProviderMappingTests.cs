using DeziroJoy.Platform.Sdl3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// How the provider takes persisted mappings from SDL, and what the scan says about them. The
/// seam is <see cref="ISdl3Api"/> plus an injected mapping source, so the ordering - mappings in
/// before the first enumeration - and the diagnostics for a file that cannot be read are
/// deterministic without SDL and without a file on disk.
/// </summary>
public sealed class Sdl3GamepadProviderMappingTests : IDisposable
{
    private readonly FakeSdl3Api _api = new();
    private readonly Sdl3GamepadProvider _provider;

    public Sdl3GamepadProviderMappingTests()
    {
        // Zero interval: every GetConnected scans, so each assertion sees the effect of the
        // scripted change without racing the rate limiter.
        _provider = new Sdl3GamepadProvider(_api, TimeSpan.Zero);
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public void Persisted_Mappings_Are_Handed_To_Sdl_Before_The_First_Enumeration()
    {
        var session = new Sdl3MappingCaptureSession();
        foreach (var step in session.Steps)
        {
            session.TryAssign(step, "b0");
        }

        var entry = new Sdl3MappingEntry
        {
            GuidString = "03000000790000000006000000000000",
            DeviceName = "Generic USB Joystick",
            Mapping = session.BuildMapping("03000000790000000006000000000000", "Generic USB Joystick", "Linux"),
            UpdatedUtc = "2026-11-16T00:00:00.0000000Z",
        };

        using var mapped = new Sdl3GamepadProvider(
            _api,
            TimeSpan.Zero,
            new FakeMappingSource([entry]));

        mapped.GetConnected();

        // The mapping must reach SDL before the device list is built: SDL decides whether a
        // joystick is a standard gamepad while enumerating, so a mapping added afterwards would
        // leave the device unrecognized until the next scan.
        Assert.Equal(entry.Mapping, Assert.Single(_api.AddedMappings));
        Assert.True(_api.GetGamepadIdsCalls >= 1);
    }

    [Fact]
    public void A_Provider_With_No_Mapping_Source_Never_Touches_A_Mapping_File()
    {
        using var bare = new Sdl3GamepadProvider(_api, TimeSpan.Zero);

        bare.GetConnected();

        Assert.Empty(_api.AddedMappings);
        Assert.Equal(0, bare.LastScanDiagnostic!.AppliedMappingCount);
        Assert.Null(bare.LastScanDiagnostic!.MappingLoadError);
    }

    [Fact]
    public void Mappings_Are_Loaded_Exactly_Once_Per_Runtime()
    {
        using var mapped = new Sdl3GamepadProvider(
            _api,
            TimeSpan.Zero,
            new FakeMappingSource([Entry("first"), Entry("second", "03000000deaded000002000000000000")]));

        mapped.GetConnected();
        mapped.GetConnected();
        mapped.GetConnected();

        Assert.Equal(2, _api.AddedMappings.Count);
        Assert.Equal(2, mapped.LastScanDiagnostic!.AppliedMappingCount);
    }

    [Fact]
    public void A_Mapping_Sdl_Rejects_Is_Counted_As_Not_Applied_And_Reported()
    {
        _api.MappingAccepted = false;
        _api.MappingRejection = "Couldn't parse GUID from test";

        using var mapped = new Sdl3GamepadProvider(
            _api,
            TimeSpan.Zero,
            new FakeMappingSource([Entry("rejected")]));

        mapped.GetConnected();

        Assert.Equal(0, mapped.LastScanDiagnostic!.AppliedMappingCount);
        Assert.False(mapped.LastScanDiagnostic!.HasAppliedMappings);
    }

    [Fact]
    public void A_Mapping_File_That_Cannot_Be_Read_Is_Reported_In_The_Scan_Guidance()
    {
        using var mapped = new Sdl3GamepadProvider(
            _api,
            TimeSpan.Zero,
            new FakeMappingSource([], "SDL 映射文件无法解析，已只使用 SDL 内置映射库：意外字符"));

        mapped.GetConnected();

        Assert.Empty(_api.AddedMappings);
        Assert.Equal(0, mapped.LastScanDiagnostic!.AppliedMappingCount);
        Assert.Contains("映射文件", mapped.LastScanDiagnostic!.MappingLoadError);
        Assert.Contains("sdl-mappings.json", mapped.LastScanDiagnostic!.Guidance);
    }

    [Fact]
    public void A_Mapped_Device_Becomes_A_Gamepad_The_Provider_Can_Read()
    {
        // The device only exists as a plain joystick until the mapping is applied; SDL then
        // lists it as a gamepad, which is what the scripted fake reproduces.
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 7, name: "DInput 手柄", serial: "unit-7"));

        using var mapped = new Sdl3GamepadProvider(
            _api,
            TimeSpan.Zero,
            new FakeMappingSource([Entry("dinput")]));

        var connected = mapped.GetConnected();

        Assert.Contains(connected, descriptor => descriptor.Name == "DInput 手柄");
        Assert.True(mapped.LastScanDiagnostic!.HasAppliedMappings);
    }

    private static Sdl3MappingEntry Entry(string name, string guid = "03000000790000000006000000000000")
        => new()
        {
            GuidString = guid,
            DeviceName = name,
            Mapping = $"{guid},{name},platform:Linux,south:b0",
            UpdatedUtc = "2026-11-16T00:00:00.0000000Z",
        };

    private sealed class FakeMappingSource : ISdl3MappingSource
    {
        private readonly IReadOnlyList<Sdl3MappingEntry> _entries;
        private readonly string? _problem;

        public FakeMappingSource(IReadOnlyList<Sdl3MappingEntry> entries, string? problem = null)
        {
            _entries = entries;
            _problem = problem;
        }

        public Sdl3MappingSnapshot Load() => new(_entries, _problem);
    }
}
