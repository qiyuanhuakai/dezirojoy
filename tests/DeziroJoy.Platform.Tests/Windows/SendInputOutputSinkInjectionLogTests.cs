using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Output;
using DeziroJoy.Platform.Windows.SendInput;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

/// <summary>
/// The always-on injection log must be a pure observer: attaching it (or any subscriber to
/// <see cref="SendInputOutputSink.InjectionEmitted"/>) may never change what reaches SendInput.
/// These tests pin that transparency and the shape of the emitted records. The native call is
/// swapped for <see cref="SendInputOutputSink.TransmitOverride"/> so the exact INPUT bytes can be
/// captured without user32.dll, which is what makes the suite runnable on Linux.
/// </summary>
public class SendInputOutputSinkInjectionLogTests
{
    [Fact]
    public void Attaching_A_Subscriber_Does_Not_Change_The_Injected_Byte_Stream()
    {
        var plain = new List<byte[]>();
        var logged = new List<byte[]>();

        using var plainSink = new SendInputOutputSink();
        plainSink.TransmitOverride = CaptureInto(plain);

        using var loggedSink = new SendInputOutputSink();
        var records = new List<InjectionRecord>();
        loggedSink.InjectionEmitted += record => records.Add(record);
        loggedSink.TransmitOverride = CaptureInto(logged);

        RunSequence(plainSink);
        RunSequence(loggedSink);

        Assert.NotEmpty(plain);
        Assert.Equal(plain.Count, logged.Count);
        Assert.Equal(plain.Count, records.Count);
        for (var i = 0; i < plain.Count; i++)
        {
            Assert.Equal(plain[i], logged[i]);
        }
    }

    [Fact]
    public void Every_Injection_Emits_A_Record_With_Action_And_Target()
    {
        using var sink = new SendInputOutputSink();
        var records = new List<InjectionRecord>();
        sink.InjectionEmitted += record => records.Add(record);
        sink.TransmitOverride = _ => 1;

        sink.KeyDown(GamepadKey.LeftAlt);
        sink.KeyDown(GamepadKey.Tab);
        sink.KeyUp(GamepadKey.Tab);
        sink.MouseMove(10, -5);
        sink.ScrollWheel(0, 2);
        sink.MouseButtonDown(MouseButton.Right);
        sink.MouseButtonUp(MouseButton.Right);

        Assert.Collection(records,
            r => Assert.Equal(InjectionAction.KeyDown, r.Action),
            r => Assert.Equal(InjectionAction.KeyDown, r.Action),
            r => Assert.Equal(InjectionAction.KeyUp, r.Action),
            r => Assert.Equal(InjectionAction.MouseMove, r.Action),
            r => Assert.Equal(InjectionAction.MouseWheel, r.Action),
            r => Assert.Equal(InjectionAction.MouseButtonDown, r.Action),
            r => Assert.Equal(InjectionAction.MouseButtonUp, r.Action));

        Assert.Equal((int)GamepadKey.LeftAlt, records[0].Primary);
        Assert.Equal((int)GamepadKey.Tab, records[1].Primary);
        Assert.Equal((int)GamepadKey.Tab, records[2].Primary);
        Assert.Equal(10, records[3].Primary);
        Assert.Equal(-5, records[3].Secondary);
        Assert.Equal(2, records[4].Primary);
        Assert.Equal(0, records[4].Secondary);
        Assert.Equal((int)MouseButton.Right, records[5].Primary);
        Assert.Equal((int)MouseButton.Right, records[6].Primary);
        Assert.All(records, r => Assert.True(r.Success));

        // The timestamp is monotonic and captured per injection.
        for (var i = 1; i < records.Count; i++)
        {
            Assert.True(records[i].Timestamp >= records[i - 1].Timestamp);
        }
    }

    [Fact]
    public void A_Failed_SendInput_Is_Recorded_As_Unsuccessful()
    {
        using var sink = new SendInputOutputSink();
        var records = new List<InjectionRecord>();
        sink.InjectionEmitted += record => records.Add(record);
        sink.TransmitOverride = _ => 0; // SendInput injected nothing.

        sink.KeyDown(GamepadKey.Tab);

        var record = Assert.Single(records);
        Assert.False(record.Success);
        Assert.True(sink.LastInjectionFailed);
    }

    [Fact]
    public void An_Unmapped_Key_Injects_Nothing_And_Emits_Nothing()
    {
        using var sink = new SendInputOutputSink();
        var records = new List<InjectionRecord>();
        sink.InjectionEmitted += record => records.Add(record);
        var transmitted = 0;
        sink.TransmitOverride = _ =>
        {
            transmitted++;
            return 1;
        };

        sink.KeyDown((GamepadKey)9999);

        Assert.Empty(records);
        Assert.Equal(0, transmitted);
    }

    private static void RunSequence(SendInputOutputSink sink)
    {
        sink.KeyDown(GamepadKey.LeftAlt);
        sink.KeyDown(GamepadKey.Tab);
        sink.KeyUp(GamepadKey.Tab);
        sink.KeyUp(GamepadKey.LeftAlt);
        sink.MouseMove(10, -5);
        sink.ScrollWheel(1, 2);
        sink.MouseButtonDown(MouseButton.Left);
        sink.MouseButtonUp(MouseButton.Left);
    }

    private static Func<INPUT, uint> CaptureInto(List<byte[]> into)
        => input =>
        {
            into.Add(ToBytes(input));
            return 1;
        };

    private static byte[] ToBytes(INPUT input)
    {
        var bytes = new byte[Unsafe.SizeOf<INPUT>()];
        MemoryMarshal.Write(bytes, in input);
        return bytes;
    }
}
