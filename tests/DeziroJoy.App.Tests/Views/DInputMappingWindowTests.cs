using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Platform.Sdl3;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

public sealed class DInputMappingWindowTests
{
    [AvaloniaFact]
    public async Task Cancel_Closes_The_Dialog_And_Completes_The_Caller()
    {
        var owner = new Window();
        owner.Show();
        var reader = new NeutralReader();
        var mapping = new DInputMappingViewModel(
            Device(),
            _ => (reader, null),
            (_, _) => new Sdl3MappingApplyResult(false, "must not apply", null),
            (_, entry) => new Sdl3MappingSaveResult(false, ["must not save"], entry),
            "test-sdl-mappings.json",
            [Sdl3MappingStep.CreateDefault()[0]],
            TimeSpan.FromMilliseconds(1));
        var dialog = new DInputMappingWindow(mapping);

        var result = dialog.ShowDialog<bool>(owner);
        Assert.True(reader.SampleStarted.Wait(TimeSpan.FromSeconds(2)));
        mapping.CancelCommand.Execute(null);

        Assert.False(await result.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(dialog.IsVisible);
        Assert.NotEqual(Environment.CurrentManagedThreadId, reader.SampleThreadId);
        owner.Close();
    }

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

    private sealed class NeutralReader : ISdl3JoystickReader
    {
        private readonly ManualResetEventSlim _releaseSample = new(false);

        public ManualResetEventSlim SampleStarted { get; } = new(false);

        public bool Disposed { get; private set; }

        public int SampleThreadId { get; private set; }

        public Sdl3JoystickSnapshot Sample()
        {
            SampleThreadId = Environment.CurrentManagedThreadId;
            SampleStarted.Set();
            _releaseSample.Wait(TimeSpan.FromSeconds(2));
            return Sdl3JoystickSnapshot.Empty;
        }

        public void Dispose()
        {
            Disposed = true;
            _releaseSample.Set();
        }
    }
}
