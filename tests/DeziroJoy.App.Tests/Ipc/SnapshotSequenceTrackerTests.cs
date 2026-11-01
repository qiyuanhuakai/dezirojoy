using DeziroJoy.App.Ipc;
using Xunit;

namespace DeziroJoy.App.Tests.Ipc;

/// <summary>
/// The GUI-side sequence policy: a snapshot that repeats or arrives late is dropped,
/// a skipped sequence is a gap that forces a re-read, and the authoritative re-sync
/// answer is adopted rather than refused as stale.
/// </summary>
public class SnapshotSequenceTrackerTests
{
    [Fact]
    public void The_First_Snapshot_Establishes_The_Baseline()
    {
        var tracker = new SnapshotSequenceTracker();

        Assert.Equal(SnapshotDisposition.Fresh, tracker.Evaluate(7));
        Assert.Equal(7, tracker.LastSequence);
    }

    [Fact]
    public void The_Next_Sequence_In_Order_Is_Fresh()
    {
        var tracker = new SnapshotSequenceTracker();
        tracker.Evaluate(7);

        Assert.Equal(SnapshotDisposition.Fresh, tracker.Evaluate(8));
        Assert.Equal(8, tracker.LastSequence);
    }

    [Fact]
    public void A_Repeated_Sequence_Is_Stale()
    {
        var tracker = new SnapshotSequenceTracker();
        tracker.Evaluate(7);

        Assert.Equal(SnapshotDisposition.Stale, tracker.Evaluate(7));
        Assert.Equal(7, tracker.LastSequence);
    }

    [Fact]
    public void An_Out_Of_Order_Snapshot_Is_Stale_And_Never_Moves_The_Baseline_Backwards()
    {
        var tracker = new SnapshotSequenceTracker();
        tracker.Evaluate(7);
        tracker.Evaluate(9);

        Assert.Equal(SnapshotDisposition.Stale, tracker.Evaluate(8));
        Assert.Equal(9, tracker.LastSequence);
    }

    [Fact]
    public void A_Skipped_Sequence_Is_A_Gap()
    {
        var tracker = new SnapshotSequenceTracker();
        tracker.Evaluate(7);

        Assert.Equal(SnapshotDisposition.Gap, tracker.Evaluate(10));
        Assert.Equal(10, tracker.LastSequence);
    }

    [Fact]
    public void Adopting_A_Resynchronized_Snapshot_Restarts_The_Baseline()
    {
        var tracker = new SnapshotSequenceTracker();
        tracker.Evaluate(7);
        tracker.Evaluate(10);

        tracker.Adopt(10);

        Assert.Equal(10, tracker.LastSequence);
        Assert.Equal(SnapshotDisposition.Fresh, tracker.Evaluate(11));
    }

    [Fact]
    public void Adopting_A_Lower_Sequence_Resets_The_Baseline_For_A_Restarted_Daemon()
    {
        var tracker = new SnapshotSequenceTracker();
        tracker.Evaluate(10);

        tracker.Adopt(0);

        Assert.Equal(0, tracker.LastSequence);
        Assert.Equal(SnapshotDisposition.Fresh, tracker.Evaluate(1));
    }
}
