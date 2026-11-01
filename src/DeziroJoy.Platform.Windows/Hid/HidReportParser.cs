using System.Buffers.Binary;

namespace DeziroJoy.Platform.Windows.Hid;

/// <summary>
/// Pure, OS-free decoding of the digital button set from a HID input report, plus the
/// classification that separates the standard 14-button Xbox layout from the non-standard
/// buttons (back paddles, macro keys) XInput cannot see. Contains no P/Invoke, so it is
/// unit-testable with synthetic reports on any OS - the same split the Linux evdev layer
/// uses between <c>EvdevEventReader</c> and the provider.
/// </summary>
/// <remarks>
/// <para>
/// Raw-code scheme: an extra button is reported as its HID button <em>usage value</em> - the
/// 1-based "Button N" number on Usage Page 0x09 (Button). The usage is defined by the device's
/// report descriptor, so it is stable across reconnects for a given pad, human-interpretable
/// ("Button 15" is the fifteenth button), and carries no guess about which logical
/// <c>GamepadButton</c> it might be. The button index would be an equally valid code; the usage
/// is preferred because it survives devices that skip usage numbers.
/// </para>
/// <para>
/// Layout assumption: buttons are one contiguous run on Usage Page 0x09, usages
/// <c>FirstUsage .. FirstUsage+ButtonCount-1</c>, packed LSB-first from <see cref="HidButtonLayout.BitOffset"/>.
/// That is the layout of essentially every gamepad; exotic multi-collection descriptors are out
/// of scope for this experimental provider.
/// </para>
/// </remarks>
public static class HidReportParser
{
    /// <summary>Usage Page 0x09 (Button). Every digital gamepad button lives here.</summary>
    public const ushort ButtonUsagePage = 0x09;

    /// <summary>
    /// Number of leading button usages treated as the standard Xbox layout
    /// (A/B/X/Y, LB/RB, Back/Start, Guide, LS/RS, DPad x4). Usages above this are the
    /// non-standard buttons this provider exists to surface; XInput already reports the rest.
    /// </summary>
    public const int StandardButtonCount = 14;

    /// <summary>
    /// Where the button bitfield sits inside an input report. <see cref="BitOffset"/> is 8 when
    /// the device prefixes every report with a 1-byte report id, else 0. <see cref="FirstUsage"/>
    /// is 1 for the conventional "Button 1..N" numbering.
    /// </summary>
    /// <param name="ButtonCount">Total digital buttons the device reports.</param>
    /// <param name="BitOffset">Bit index within the report where the button bitfield starts.</param>
    /// <param name="FirstUsage">HID usage value of the first button (usually 1).</param>
    public readonly record struct HidButtonLayout(int ButtonCount, int BitOffset, int FirstUsage);

    /// <summary>
    /// Decodes every pressed button usage from <paramref name="report"/>. Bit N of the bitfield
    /// (counted from <see cref="HidButtonLayout.BitOffset"/>, LSB-first within each byte) is
    /// "Button (FirstUsage + N)". A report too short to hold the whole bitfield is treated as
    /// malformed and yields nothing rather than throwing.
    /// </summary>
    public static List<ushort> DecodeButtonUsages(ReadOnlySpan<byte> report, HidButtonLayout layout)
    {
        var usages = new List<ushort>();

        var count = layout.ButtonCount;
        if (count <= 0 || report.IsEmpty)
        {
            return usages;
        }

        var firstUsage = layout.FirstUsage <= 0 ? 1 : layout.FirstUsage;
        var requiredBytes = (layout.BitOffset + count + 7) / 8;
        if (report.Length < requiredBytes)
        {
            // Truncated / malformed report: refuse to guess at the missing bits.
            return usages;
        }

        for (var i = 0; i < count; i++)
        {
            var bit = layout.BitOffset + i;
            var mask = (byte)(1 << (bit & 7));
            if ((report[bit >> 3] & mask) != 0)
            {
                usages.Add((ushort)(firstUsage + i));
            }
        }

        return usages;
    }

    /// <summary>
    /// Returns the non-standard buttons currently pressed as raw codes, sorted ascending and
    /// de-duplicated. Usages at or below <paramref name="standardButtonCount"/> are the standard
    /// Xbox layout and are intentionally excluded. Never throws; a malformed or truncated report
    /// yields an empty list.
    /// </summary>
    public static IReadOnlyList<int> ExtractExtraButtons(
        ReadOnlySpan<byte> report,
        HidButtonLayout layout,
        int standardButtonCount = StandardButtonCount)
    {
        var usages = DecodeButtonUsages(report, layout);
        if (usages.Count == 0)
        {
            return Array.Empty<int>();
        }

        var extras = new List<int>();
        foreach (var usage in usages)
        {
            if (usage > standardButtonCount)
            {
                extras.Add(usage);
            }
        }

        extras.Sort();
        return extras;
    }

    /// <summary>
    /// Classifies an already-decoded set of pressed usages (as produced by
    /// <see cref="DecodeButtonUsages"/> or by <c>HidP_GetUsages</c>) into the non-standard subset.
    /// Kept separate so callers that decode through HidP still share one classification rule.
    /// </summary>
    public static IReadOnlyList<int> ExtractExtraButtons(
        ReadOnlySpan<ushort> usages,
        int standardButtonCount = StandardButtonCount)
    {
        if (usages.IsEmpty)
        {
            return Array.Empty<int>();
        }

        var extras = new List<int>();
        foreach (var usage in usages)
        {
            if (usage > standardButtonCount)
            {
                extras.Add(usage);
            }
        }

        extras.Sort();
        return extras;
    }
}
