using DeziroJoy.App.ViewModels;
using DeziroJoy.Platform.Sdl3;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public sealed class DInputMappingViewModelTests
{
    private static readonly IReadOnlyList<Sdl3MappingStep> TwoSteps =
    [
        new("a", "面键 下", Sdl3MappingStepRole.Button, true, true, true, true),
        new("lefttrigger", "左扳机", Sdl3MappingStepRole.Trigger, true, true, true, true),
    ];

    [Fact]
    public async Task Captures_Saves_And_Closes_With_A_Mapping_That_Sdl_Accepted()
    {
        var reader = new ScriptedReader(
            Sdl3JoystickSnapshot.Empty,
            new([true], [], []),
            Sdl3JoystickSnapshot.Empty,
            Sdl3JoystickSnapshot.Empty);
        string? applied = null;
        Sdl3MappingEntry? saved = null;
        var vm = Create(reader, (mapping, _) =>
        {
            applied = mapping;
            return new Sdl3MappingApplyResult(true, null, "registered");
        }, (_, entry) =>
        {
            saved = entry;
            return new Sdl3MappingSaveResult(true, [], entry);
        });
        var closes = new List<bool>();
        vm.CloseRequested += closes.Add;
        vm.SkipCommand.Execute(null);

        Assert.True(await vm.RunAsync());
        Assert.Equal([true], closes);
        Assert.NotNull(saved);
        Assert.Equal(saved.Mapping, applied);
        Assert.Contains("a:b0", saved.Mapping);
        Assert.False(vm.IsCapturing);
        Assert.True(reader.Disposed);
    }

    [Fact]
    public async Task Cancel_Abandons_The_Flow_Without_Applying_Or_Saving()
    {
        var reader = new ScriptedReader(Sdl3JoystickSnapshot.Empty);
        var applyCalls = 0;
        var saveCalls = 0;
        var vm = Create(reader, (_, _) =>
        {
            applyCalls++;
            return new Sdl3MappingApplyResult(true, null, null);
        }, (_, entry) =>
        {
            saveCalls++;
            return new Sdl3MappingSaveResult(true, [], entry);
        });
        vm.CancelCommand.Execute(null);

        Assert.False(await vm.RunAsync());
        Assert.Equal(0, applyCalls);
        Assert.Equal(0, saveCalls);
        Assert.Contains("未写入", vm.StatusMessage);
    }

    [Fact]
    public async Task Sdl_Rejection_Is_Reported_And_Nothing_Is_Persisted()
    {
        var reader = new ScriptedReader(
            Sdl3JoystickSnapshot.Empty,
            new([true], [], []),
            Sdl3JoystickSnapshot.Empty,
            Sdl3JoystickSnapshot.Empty);
        var saveCalls = 0;
        var vm = Create(reader, (_, _) => new Sdl3MappingApplyResult(false, "bad mapping", null), (_, entry) =>
        {
            saveCalls++;
            return new Sdl3MappingSaveResult(true, [], entry);
        });
        var closes = new List<bool>();
        vm.CloseRequested += closes.Add;
        vm.SkipCommand.Execute(null);

        Assert.False(await vm.RunAsync());
        Assert.Equal(0, saveCalls);
        Assert.Contains("bad mapping", vm.StatusMessage);
        Assert.Empty(closes);
        vm.CancelCommand.Execute(null);
        Assert.Equal([false], closes);
    }

    private static DInputMappingViewModel Create(
        ISdl3JoystickReader reader,
        Func<string, string, Sdl3MappingApplyResult> apply,
        Func<string, Sdl3MappingEntry, Sdl3MappingSaveResult> save)
        => new(
            Device(),
            _ => (reader, null),
            apply,
            save,
            "test-sdl-mappings.json",
            TwoSteps,
            TimeSpan.Zero);

    private static Sdl3JoystickInfo Device() => new()
    {
        InstanceId = 1,
        Name = "DInput test pad",
        GuidString = "03000000373500004110000000000000",
        Vendor = 0x3537,
        Product = 0x1041,
        ProductVersion = 0,
        Serial = string.Empty,
        Path = "test-path",
        ButtonCount = 19,
        AxisCount = 6,
        HatCount = 1,
        TypeName = "Unknown",
        HasStandardMapping = false,
    };

    private sealed class ScriptedReader(params Sdl3JoystickSnapshot[] samples) : ISdl3JoystickReader
    {
        private readonly Queue<Sdl3JoystickSnapshot> _samples = new(samples);
        private Sdl3JoystickSnapshot _last = Sdl3JoystickSnapshot.Empty;

        public bool Disposed { get; private set; }

        public Sdl3JoystickSnapshot Sample()
        {
            if (_samples.Count > 0)
            {
                _last = _samples.Dequeue();
            }

            return _last;
        }

        public void Dispose() => Disposed = true;
    }
}
