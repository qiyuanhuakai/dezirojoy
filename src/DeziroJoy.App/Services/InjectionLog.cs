using System.Diagnostics;
using System.Text;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Output;

namespace DeziroJoy.App.Services;

/// <summary>
/// Always-on, fixed-size ring buffer of injected events. The engine thread writes through
/// <see cref="Record"/>; the UI thread reads through <see cref="Snapshot"/> / <see cref="DumpLast"/>.
/// A single lock guards both, so a reader never observes a torn or half-written entry. The buffer
/// is preallocated once and never grows, so a long session cannot leak; the oldest entry is
/// overwritten when it fills.
/// </summary>
/// <remarks>
/// This is a diagnostic observer only. It never feeds a clock or a value back into the mapping
/// path - the engine's timing stays driven purely by its own deltaSeconds - so enabling it cannot
/// change what is injected, when, or whether it succeeds.
/// </remarks>
public sealed class InjectionLog
{
    private readonly Lock _sync = new();
    private readonly InjectionRecord[] _buffer;
    private readonly int _capacity;

    private long _nextIndex;
    private long _baseline;

    /// <param name="capacity">How many entries to retain. A few hundred is plenty for a session.</param>
    public InjectionLog(int capacity = 512)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _buffer = new InjectionRecord[capacity];
    }

    /// <summary>How many entries the buffer retains before it starts dropping the oldest.</summary>
    public int Capacity => _capacity;

    /// <summary>How many entries are currently held (never exceeds <see cref="Capacity"/>).</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return (int)Math.Min(_nextIndex, _capacity);
            }
        }
    }

    /// <summary>
    /// Appends one record. Called from the engine thread via the sink's <c>InjectionEmitted</c>
    /// event. Overwrites the oldest entry once the buffer is full.
    /// </summary>
    public void Record(InjectionRecord record)
    {
        lock (_sync)
        {
            if (_nextIndex == 0)
            {
                _baseline = record.Timestamp;
            }

            _buffer[_nextIndex % _capacity] = record;
            _nextIndex++;
        }
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> of the most recent records, oldest first, as a
    /// consistent snapshot copied under the lock.
    /// </summary>
    public IReadOnlyList<InjectionRecord> Snapshot(int count)
    {
        lock (_sync)
        {
            return CopyLast(count, out _);
        }
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> of the most recent entries as newline-terminated text
    /// lines, e.g. <c>00:00:12.345  ↓ LeftAlt   (ok)</c>. This is the member to bind in the UI.
    /// </summary>
    public string DumpLast(int count)
    {
        InjectionRecord[] records;
        long baseline;
        lock (_sync)
        {
            records = CopyLast(count, out baseline);
        }

        var text = new StringBuilder(records.Length * 48);
        foreach (var record in records)
        {
            text.Append(FormatLine(record, baseline)).Append('\n');
        }

        return text.ToString();
    }

    private InjectionRecord[] CopyLast(int count, out long baseline)
    {
        var total = _nextIndex;
        var valid = (int)Math.Min(total, _capacity);
        var take = Math.Min(Math.Max(count, 0), valid);
        var result = new InjectionRecord[take];
        var start = total - take;
        for (var i = 0; i < take; i++)
        {
            result[i] = _buffer[(start + i) % _capacity];
        }

        baseline = _baseline;
        return result;
    }

    private static string FormatLine(InjectionRecord record, long baseline)
    {
        var elapsed = Elapsed(record.Timestamp, baseline);
        var status = record.Success ? "(ok)" : "(FAIL)";
        return $"{elapsed:hh\\:mm\\:ss\\.fff}  {Describe(record),-16}{status}";
    }

    private static TimeSpan Elapsed(long timestamp, long baseline)
    {
        var delta = timestamp - baseline;
        if (delta < 0)
        {
            delta = 0;
        }

        return TimeSpan.FromSeconds((double)delta / Stopwatch.Frequency);
    }

    private static string Describe(InjectionRecord record) => record.Action switch
    {
        InjectionAction.KeyDown => $"↓ {KeyName(record.Primary)}",
        InjectionAction.KeyUp => $"↑ {KeyName(record.Primary)}",
        InjectionAction.MouseButtonDown => $"↓ mouse:{ButtonName(record.Primary)}",
        InjectionAction.MouseButtonUp => $"↑ mouse:{ButtonName(record.Primary)}",
        InjectionAction.MouseMove => $"→ move ({record.Primary},{record.Secondary})",
        InjectionAction.MouseWheel => $"⟳ wheel (v={record.Primary},h={record.Secondary})",
        _ => record.Action.ToString(),
    };

    private static string KeyName(int key) => Enum.IsDefined((GamepadKey)key) ? ((GamepadKey)key).ToString() : $"vk 0x{key:X}";

    private static string ButtonName(int button) => Enum.IsDefined((MouseButton)button) ? ((MouseButton)button).ToString() : $"button {button}";
}
