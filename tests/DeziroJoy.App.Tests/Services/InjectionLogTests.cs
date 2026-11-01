using DeziroJoy.App.Services;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Output;
using System.Diagnostics;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The injection log is a fixed-size ring buffer written by the engine thread and read by the
/// UI thread. These tests pin the ordering, the drop-oldest behaviour, the snapshot text format,
/// and that a concurrent writer/reader never tears or throws. Records are synthesised directly so
/// the log is exercised without a live sink.
/// </summary>
public class InjectionLogTests
{
    [Fact]
    public void Records_Are_Returned_In_Order()
    {
        var log = new InjectionLog();
        log.Record(new InjectionRecord(1, InjectionAction.KeyDown, (int)GamepadKey.LeftAlt, 0, true));
        log.Record(new InjectionRecord(2, InjectionAction.KeyDown, (int)GamepadKey.Tab, 0, true));
        log.Record(new InjectionRecord(3, InjectionAction.KeyUp, (int)GamepadKey.Tab, 0, false));

        var snapshot = log.Snapshot(10);

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(InjectionAction.KeyDown, snapshot[0].Action);
        Assert.Equal((int)GamepadKey.LeftAlt, snapshot[0].Primary);
        Assert.Equal(InjectionAction.KeyDown, snapshot[1].Action);
        Assert.Equal((int)GamepadKey.Tab, snapshot[1].Primary);
        Assert.Equal(InjectionAction.KeyUp, snapshot[2].Action);
        Assert.False(snapshot[2].Success);
    }

    [Fact]
    public void Ring_Buffer_Drops_The_Oldest_When_Full()
    {
        var log = new InjectionLog(capacity: 3);
        for (var i = 0; i < 5; i++)
        {
            log.Record(new InjectionRecord(1000 + i, InjectionAction.KeyDown, (int)GamepadKey.A + i, 0, true));
        }

        Assert.Equal(3, log.Count);
        var snapshot = log.Snapshot(10);
        Assert.Equal(3, snapshot.Count);
        Assert.Equal((int)GamepadKey.A + 2, snapshot[0].Primary);
        Assert.Equal((int)GamepadKey.A + 3, snapshot[1].Primary);
        Assert.Equal((int)GamepadKey.A + 4, snapshot[2].Primary);
    }

    [Fact]
    public void Snapshot_Formats_Each_Line_With_Timestamp_Arrow_And_Status()
    {
        var log = new InjectionLog();
        var baseTs = 1_000_000L;
        log.Record(new InjectionRecord(baseTs, InjectionAction.KeyDown, (int)GamepadKey.LeftAlt, 0, true));
        log.Record(new InjectionRecord(baseTs + Stopwatch.Frequency / 2, InjectionAction.KeyDown, (int)GamepadKey.Tab, 0, true));

        var lines = log.DumpLast(10).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3}  ↓ LeftAlt\s+\(ok\)$", lines[0]);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3}  ↓ Tab\s+\(ok\)$", lines[1]);
        Assert.StartsWith("00:00:00.000", lines[0]);
        Assert.StartsWith("00:00:00.500", lines[1]);
    }

    [Fact]
    public void A_Failed_Injection_Is_Marked_In_The_Snapshot()
    {
        var log = new InjectionLog();
        log.Record(new InjectionRecord(1, InjectionAction.KeyDown, (int)GamepadKey.LeftAlt, 0, false));

        var line = log.DumpLast(10);

        Assert.Contains("(FAIL)", line);
        Assert.DoesNotContain("(ok)", line);
    }

    [Fact]
    public void DumpLast_Returns_Only_The_Requested_Number_Of_Entries()
    {
        var log = new InjectionLog();
        for (var i = 0; i < 10; i++)
        {
            log.Record(new InjectionRecord(1000 + i, InjectionAction.KeyDown, (int)GamepadKey.A, 0, true));
        }

        var lines = log.DumpLast(3).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Concurrent_Writes_And_Reads_Do_Not_Throw_Or_Tear()
    {
        var log = new InjectionLog(capacity: 256);
        const int writers = 4;
        const int perWriter = 2000;
        Exception? captured = null;
        var failureLock = new object();
        var gate = new ManualResetEventSlim(false);

        void Guard(Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                lock (failureLock)
                {
                    captured ??= ex;
                }
            }
        }

        var threads = new List<Thread>();
        for (var w = 0; w < writers; w++)
        {
            var t = new Thread(() => Guard(() =>
            {
                gate.Wait();
                for (var i = 0; i < perWriter; i++)
                {
                    log.Record(new InjectionRecord(i, InjectionAction.MouseMove, i, -i, true));
                }
            }));
            threads.Add(t);
            t.Start();
        }

        var reader = new Thread(() => Guard(() =>
        {
            gate.Wait();
            for (var i = 0; i < 1000; i++)
            {
                var dump = log.DumpLast(50);
                foreach (var line in dump.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains("(ok)") && !line.Contains("(FAIL)"))
                    {
                        throw new InvalidOperationException("torn line: " + line);
                    }
                }
            }
        }));
        threads.Add(reader);
        reader.Start();

        gate.Set();
        foreach (var t in threads)
        {
            t.Join();
        }

        Assert.Null(captured);
        Assert.Equal(Math.Min(writers * perWriter, 256), log.Count);
    }

    [Fact]
    public void Composition_Exposes_An_InjectionLog()
    {
        using var composition = new EngineComposition();

        Assert.NotNull(composition.InjectionLog);
    }
}
