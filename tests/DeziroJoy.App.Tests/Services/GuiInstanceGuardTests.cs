using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// GUI single-instance arbitration. Exactly one GUI per user/session: the first launch wins
/// the instance lease and serves the activation channel; every later launch asks the winner
/// to show itself and then exits - before any Avalonia or runtime construction. A launch that
/// cannot reach the winner exits too: a failed activation is never a reason to start a second
/// GUI.
/// </summary>
public sealed class GuiInstanceGuardTests
{
    private static GuiInstanceIdentity NewIdentity()
    {
        var id = Guid.NewGuid().ToString("N");
        return new GuiInstanceIdentity(
            Path.Combine(Path.GetTempPath(), $"dezirojoy-test-gui-{id}.lock"),
            $"DeziroJoy.Test.Gui.Activation.{id}");
    }

    private static readonly GuiActivationOptions FastOptions = new(
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromSeconds(5));

    [Fact]
    public async Task The_First_Launch_Wins_The_Instance_Lease()
    {
        var identity = NewIdentity();

        var arbitration = await GuiInstanceGuard.ArbitrateAsync(identity, FastOptions, TestContext.Current.CancellationToken);

        Assert.True(arbitration.IsWinner, "the first launch must own the instance");
        using var lease = arbitration.Lease;
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task A_Second_Launch_Asks_The_Winner_To_Show_And_Exits()
    {
        var identity = NewIdentity();
        using var lease = (await GuiInstanceGuard.ArbitrateAsync(identity, FastOptions, TestContext.Current.CancellationToken)).Lease!;
        using var server = new GuiActivationServer(identity);

        var shows = 0;
        server.ShowRequested += () =>
        {
            shows++;
            return Task.CompletedTask;
        };

        var second = await GuiInstanceGuard.ArbitrateAsync(identity, FastOptions, TestContext.Current.CancellationToken);

        Assert.False(second.IsWinner, "the second launch must not own the instance");
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(1, shows);
    }

    [Fact]
    public async Task A_Launch_That_Cannot_Reach_The_Winner_Exits_Instead_Of_Starting_A_Second_Gui()
    {
        var identity = NewIdentity();

        // The lease is held but nothing serves the activation channel: the owner is wedged.
        // The launch must exit rather than construct a second GUI.
        using var lease = GuiInstanceLease.TryAcquire(identity);
        Assert.NotNull(lease);

        var arbitration = await GuiInstanceGuard.ArbitrateAsync(identity, FastOptions, TestContext.Current.CancellationToken);

        Assert.False(arbitration.IsWinner, "a failed activation must never yield a second GUI");
        Assert.NotEqual(0, arbitration.ExitCode);
    }

    [Fact]
    public async Task Releasing_The_Lease_Lets_The_Next_Launch_Win()
    {
        var identity = NewIdentity();

        var first = await GuiInstanceGuard.ArbitrateAsync(identity, FastOptions, TestContext.Current.CancellationToken);
        Assert.True(first.IsWinner);
        first.Lease!.Dispose();

        var second = await GuiInstanceGuard.ArbitrateAsync(identity, FastOptions, TestContext.Current.CancellationToken);

        Assert.True(second.IsWinner, "a released lease must let the next launch own the instance");
        second.Lease!.Dispose();
    }

    [Fact]
    public void The_Lease_Excludes_A_Second_Acquirer_In_The_Same_Process()
    {
        var identity = NewIdentity();

        using var lease = GuiInstanceLease.TryAcquire(identity);
        Assert.NotNull(lease);

        Assert.Null(GuiInstanceLease.TryAcquire(identity));
    }

    [Fact]
    public void The_Instance_Owner_Can_Roll_Back_A_Failed_Handover()
    {
        var identity = NewIdentity();
        var lease = GuiInstanceLease.TryAcquire(identity);
        Assert.NotNull(lease);
        using var owner = new GuiInstanceOwner(identity, lease, new GuiActivationServer(identity));

        owner.ReleaseLease();
        using (var successor = GuiInstanceLease.TryAcquire(identity))
        {
            Assert.NotNull(successor);
            Assert.False(owner.TryReacquireLease());
        }

        Assert.True(owner.TryReacquireLease());
        Assert.Null(GuiInstanceLease.TryAcquire(identity));
    }
}
