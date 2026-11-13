using System.Runtime.InteropServices;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Output;
using DeziroJoy.Platform.Windows.SendInput;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

/// <summary>
/// The text path. A short string is typed as one KEYEVENTF_UNICODE event per UTF-16 code
/// unit, so a character the local layout cannot name still reaches the foreground window;
/// the native call is swapped for <see cref="SendInputOutputSink.TransmitOverride"/> so the
/// exact INPUT bytes are captured without user32.dll, which is what makes this runnable
/// off Windows.
/// </summary>
public class SendInputOutputSinkTextTests
{
    /// <summary>KEYEVENTF_UNICODE, the flag the sink sets on every unit it types.</summary>
    private const uint KeyEventFUnicode = 0x0004;

    [Fact]
    public void Text_Is_Typed_As_One_Unicode_Event_Per_Code_Unit()
    {
        var sent = new List<INPUT>();
        using var sink = new SendInputOutputSink();
        sink.TransmitOverride = input =>
        {
            sent.Add(input);
            return 1;
        };

        sink.SendText("aA1");

        Assert.Equal(3, sent.Count);
        Assert.All(sent, input =>
        {
            Assert.Equal(SendInputNative.InputKeyboard, input.Type);
            Assert.Equal(KeyEventFUnicode, input.Keyboard.DwFlags);
            Assert.Equal(0u, input.Keyboard.WVk);
        });

        // The character itself rides in wScan, never in a virtual key.
        Assert.Equal('a', sent[0].Keyboard.WScan);
        Assert.Equal('A', sent[1].Keyboard.WScan);
        Assert.Equal('1', sent[2].Keyboard.WScan);
    }

    [Fact]
    public void A_Non_Ascii_Character_Types_Through_The_Unicode_Flag()
    {
        // The point of the path: no virtual key names 世, so it can only travel as a
        // character. A surrogate pair goes out as its two code units, the way a real
        // keyboard's WM_CHAR stream delivers it.
        var sent = new List<INPUT>();
        using var sink = new SendInputOutputSink();
        sink.TransmitOverride = input =>
        {
            sent.Add(input);
            return 1;
        };

        sink.SendText("世\U0001F600");

        Assert.Equal(3, sent.Count);
        Assert.Equal('世', sent[0].Keyboard.WScan);
        Assert.Equal('\uD83D', sent[1].Keyboard.WScan);
        Assert.Equal('\uDE00', sent[2].Keyboard.WScan);
    }

    [Fact]
    public void An_Empty_String_Injects_Nothing()
    {
        var transmitted = 0;
        using var sink = new SendInputOutputSink();
        sink.TransmitOverride = _ =>
        {
            transmitted++;
            return 1;
        };

        sink.SendText(string.Empty);

        Assert.Equal(0, transmitted);
    }

    [Fact]
    public void Every_Unit_Emits_Its_Own_Injection_Record()
    {
        using var sink = new SendInputOutputSink();
        var records = new List<InjectionRecord>();
        sink.InjectionEmitted += record => records.Add(record);
        sink.TransmitOverride = _ => 1;

        sink.SendText("hi");

        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal(InjectionAction.Text, record.Action));
        Assert.Equal('h', records[0].Primary);
        Assert.Equal('i', records[1].Primary);
    }

    [Fact]
    public void A_Blocked_Text_Injection_Is_Reported_Like_Any_Other()
    {
        // UIPI blocking is per injection, so a dropped character must surface rather than
        // silently trimming the string.
        using var sink = new SendInputOutputSink();
        var failures = new List<SendInputFailureEventArgs>();
        sink.InjectionFailed += (_, args) => failures.Add(args);
        sink.TransmitOverride = _ => 0;

        sink.SendText("ab");

        Assert.Equal(2, failures.Count);
        Assert.Equal(2, sink.FailedInjectionCount);
    }
}
