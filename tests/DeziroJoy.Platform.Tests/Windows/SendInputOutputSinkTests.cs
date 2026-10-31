using DeziroJoy.Platform.Windows.Output;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

/// <summary>
/// SendInput is subject to UIPI: a medium-integrity process cannot inject into a
/// high-integrity one, and the failure used to be swallowed whole. These tests pin the
/// surfacing contract - the return value and the marshalled GetLastError must reach the
/// caller instead of vanishing.
/// </summary>
public class SendInputOutputSinkTests
{
    [Fact]
    public void A_Blocked_Injection_Is_Reported_Instead_Of_Being_Swallowed()
    {
        using var sink = new SendInputOutputSink();
        SendInputFailureEventArgs? failure = null;
        sink.InjectionFailed += (_, args) => failure = args;

        // SendInput returns the number of events it actually injected. Zero of one
        // requested is exactly what UIPI blocking (and every other failure) looks like.
        sink.ReportInjectionResult(injected: 0, requested: 1, errorCode: 5);

        Assert.True(sink.LastInjectionFailed);
        Assert.Equal(5, sink.LastErrorCode);
        Assert.Equal(1, sink.FailedInjectionCount);
        Assert.NotNull(failure);
        Assert.Equal(0u, failure!.InjectedCount);
        Assert.Equal(1u, failure.RequestedCount);
        Assert.Equal(5, failure.ErrorCode);
    }

    [Fact]
    public void A_Successful_Injection_Clears_The_Failure_State()
    {
        using var sink = new SendInputOutputSink();
        sink.ReportInjectionResult(injected: 0, requested: 1, errorCode: 5);
        Assert.True(sink.LastInjectionFailed);

        sink.ReportInjectionResult(injected: 1, requested: 1, errorCode: 0);

        Assert.False(sink.LastInjectionFailed);
        Assert.Equal(0, sink.LastErrorCode);
        Assert.Equal(1, sink.FailedInjectionCount);
    }

    [Fact]
    public void No_Event_Is_Raised_When_Injection_Succeeds()
    {
        using var sink = new SendInputOutputSink();
        var raised = 0;
        sink.InjectionFailed += (_, _) => raised++;

        sink.ReportInjectionResult(injected: 1, requested: 1, errorCode: 0);

        Assert.Equal(0, raised);
        Assert.False(sink.LastInjectionFailed);
        Assert.Equal(0, sink.FailedInjectionCount);
    }

    [Fact]
    public void Every_Blocked_Injection_Raises_Its_Own_Event()
    {
        using var sink = new SendInputOutputSink();
        var failures = new List<SendInputFailureEventArgs>();
        sink.InjectionFailed += (_, args) => failures.Add(args);

        sink.ReportInjectionResult(injected: 0, requested: 1, errorCode: 5);
        sink.ReportInjectionResult(injected: 0, requested: 1, errorCode: 5);

        Assert.Equal(2, failures.Count);
        Assert.Equal(2, sink.FailedInjectionCount);
    }

    [Fact]
    public void A_Partially_Injected_Batch_Counts_As_A_Failure()
    {
        using var sink = new SendInputOutputSink();
        SendInputFailureEventArgs? failure = null;
        sink.InjectionFailed += (_, args) => failure = args;

        sink.ReportInjectionResult(injected: 1, requested: 4, errorCode: 0);

        Assert.True(sink.LastInjectionFailed);
        Assert.NotNull(failure);
        Assert.Equal(1u, failure!.InjectedCount);
        Assert.Equal(4u, failure.RequestedCount);
    }
}
