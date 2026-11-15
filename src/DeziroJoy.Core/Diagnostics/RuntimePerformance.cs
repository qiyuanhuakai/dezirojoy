using System.Diagnostics;

namespace DeziroJoy.Core.Diagnostics;

/// <summary>
/// Opt-in wall-clock measurements for the performance baseline. Disabled unless
/// DEZIROJOY_PERF_METRICS=1, so the normal input path pays no timing or locking cost.
/// Percentiles use fixed 0.01 ms bins up to 40.96 ms; larger samples retain their exact
/// contribution to count/mean/max and are reported as overflow. No hot-path allocation occurs.
/// </summary>
public static class RuntimePerformance
{
    private const double BinWidthMs = 0.01;
    private const int BinCount = 4096;

    private static readonly Lock Sync = new();
    private static readonly Histogram Ticks = new();
    private static readonly Histogram InputLatencies = new();
    private static long _injectionSuccessCount;
    private static long _injectionFailureCount;

    [ThreadStatic]
    private static long _inputStarted;

    [ThreadStatic]
    private static bool _injectionRecorded;

    public static bool Enabled { get; } =
        string.Equals(Environment.GetEnvironmentVariable("DEZIROJOY_PERF_METRICS"), "1", StringComparison.Ordinal);

    public static void BeginInputSample()
    {
        if (!Enabled)
        {
            return;
        }

        _inputStarted = Stopwatch.GetTimestamp();
        _injectionRecorded = false;
    }

    public static void EndInputSample()
    {
        _inputStarted = 0;
        _injectionRecorded = false;
    }

    public static void RecordTick(TimeSpan interval)
    {
        if (!Enabled)
        {
            return;
        }

        Add(Ticks, interval.TotalMilliseconds);
    }

    /// <summary>Records the first completed native injection for the current provider poll.</summary>
    public static void RecordInjection(bool success)
    {
        if (!Enabled)
        {
            return;
        }

        if (success)
        {
            Interlocked.Increment(ref _injectionSuccessCount);
        }
        else
        {
            Interlocked.Increment(ref _injectionFailureCount);
        }

        if (_inputStarted == 0 || _injectionRecorded)
        {
            return;
        }

        _injectionRecorded = true;
        var elapsedMs = (Stopwatch.GetTimestamp() - _inputStarted) * 1000d / Stopwatch.Frequency;
        Add(InputLatencies, elapsedMs);
    }

    public static RuntimePerformanceSnapshot Snapshot(string role, string scenario)
    {
        lock (Sync)
        {
            return new RuntimePerformanceSnapshot(
                DateTimeOffset.UtcNow,
                Environment.ProcessId,
                role,
                scenario,
                GC.GetTotalAllocatedBytes(false),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                Interlocked.Read(ref _injectionSuccessCount),
                Interlocked.Read(ref _injectionFailureCount),
                Ticks.Snapshot(),
                InputLatencies.Snapshot());
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            Ticks.Clear();
            InputLatencies.Clear();
            Interlocked.Exchange(ref _injectionSuccessCount, 0);
            Interlocked.Exchange(ref _injectionFailureCount, 0);
        }
    }

    private static void Add(Histogram histogram, double valueMs)
    {
        lock (Sync)
        {
            histogram.Add(valueMs);
        }
    }

    private sealed class Histogram
    {
        private readonly long[] _bins = new long[BinCount];
        private long _count;
        private long _overflow;
        private double _sum;
        private double _max;

        public void Add(double valueMs)
        {
            if (double.IsNaN(valueMs) || valueMs < 0)
            {
                valueMs = 0;
            }

            _count++;
            _sum += valueMs;
            _max = Math.Max(_max, valueMs);
            var index = (int)(valueMs / BinWidthMs);
            if (index >= BinCount)
            {
                _overflow++;
            }
            else
            {
                _bins[index]++;
            }
        }

        public void Clear()
        {
            Array.Clear(_bins);
            _count = 0;
            _overflow = 0;
            _sum = 0;
            _max = 0;
        }

        public PerformanceMetricSnapshot Snapshot() => new(
            _count,
            _count == 0 ? 0 : _sum / _count,
            Percentile(0.50),
            Percentile(0.95),
            Percentile(0.99),
            _max,
            _overflow,
            BinWidthMs,
            BinCount * BinWidthMs);

        private double Percentile(double fraction)
        {
            if (_count == 0)
            {
                return 0;
            }

            var wanted = Math.Max(1, (long)Math.Ceiling(_count * fraction));
            long seen = 0;
            for (var i = 0; i < _bins.Length; i++)
            {
                seen += _bins[i];
                if (seen >= wanted)
                {
                    return (i + 1) * BinWidthMs;
                }
            }

            // Overflow has no exact percentile; max is an honest lower-bound marker together
            // with OverflowCount, rather than a fabricated 40.96 ms ceiling.
            return _max;
        }
    }
}

public sealed record PerformanceMetricSnapshot(
    long Count,
    double MeanMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs,
    long OverflowCount,
    double BinWidthMs,
    double LinearRangeMs);

public sealed record RuntimePerformanceSnapshot(
    DateTimeOffset TimestampUtc,
    int ProcessId,
    string Role,
    string Scenario,
    long TotalAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long InjectionSuccessCount,
    long InjectionFailureCount,
    PerformanceMetricSnapshot TickInterval,
    PerformanceMetricSnapshot InputToInjection);
