using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Platform.Linux;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The status area must explain an empty device table, not just announce it. "Connect a
/// gamepad" is useless advice when the nodes are unreadable or absent, so the probe's wording
/// is quoted - and a probe that has nothing to say leaves the original line untouched. A sink
/// that failed to create is quoted too, with a pad connected as well: a healthy device line
/// over dead injection is how "mapping does nothing" shipped unnoticed.
/// </summary>
public class MainWindowViewModelDiagnosticTests
{
    private static MainWindowViewModel CreateView(
        Func<LinuxRuntimeHints>? runtimeHints = null,
        bool daemonRequired = false,
        string? sinkError = null)
    {
        var store = new InMemoryProfileStore();
        var host = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);

        return new MainWindowViewModel(
            host,
            store,
            daemonRequired: daemonRequired,
            runtimeHints: runtimeHints,
            sinkError: sinkError);
    }

    [Fact]
    public void Without_Hints_The_Line_Stays_The_Plain_Connect_A_Gamepad()
    {
        var vm = CreateView(runtimeHints: static () => new LinuxRuntimeHints(null, null));

        Assert.Equal("未检测到手柄，请连接手柄", vm.StatusMessage);
    }

    [Fact]
    public void A_Device_Permission_Problem_Is_Quoted_In_The_Status()
    {
        var vm = CreateView(runtimeHints: static () => new LinuxRuntimeHints(
            "权限不足（errno 13）：sudo usermod -aG input $USER 后重新登录",
            null));

        Assert.StartsWith("未检测到手柄，请连接手柄", vm.StatusMessage);
        Assert.Contains("errno 13", vm.StatusMessage);
        Assert.Contains("usermod -aG input", vm.StatusMessage);
    }

    [Fact]
    public void An_Unwritable_Uinput_Is_Named_As_Injection_Unavailable()
    {
        var vm = CreateView(runtimeHints: static () => new LinuxRuntimeHints(
            null,
            "无法打开 /dev/uinput（errno 13）：请确认 uinput 模块已加载，且当前用户有写权限。"));

        Assert.Contains("注入不可用", vm.StatusMessage);
        Assert.Contains("/dev/uinput", vm.StatusMessage);
    }

    [Fact]
    public void Both_Hints_Are_Quoted_Together()
    {
        var vm = CreateView(runtimeHints: static () => new LinuxRuntimeHints(
            "未找到手柄节点，请检查连接与蓝牙配对",
            "无法打开 /dev/uinput（errno 2）"));

        Assert.Contains("未找到手柄节点", vm.StatusMessage);
        Assert.Contains("注入不可用", vm.StatusMessage);
    }

    [Fact]
    public void A_Probe_That_Throws_Never_Breaks_The_Status_Line()
    {
        var vm = CreateView(runtimeHints: static () => throw new IOException("probe failed"));

        Assert.Equal("未检测到手柄，请连接手柄", vm.StatusMessage);
    }

    [Fact]
    public void A_Sink_Error_Stays_On_The_Status_Line_With_A_Pad_Connected()
    {
        var store = new InMemoryProfileStore();
        var provider = new FakeGamepadProvider();
        provider.Add("/dev/input/event3", "Generic X-Box pad");
        var host = new EngineHost(provider, new RecordingOutputSink(), store);
        var vm = new MainWindowViewModel(
            host,
            store,
            sinkError: "无法打开 /dev/uinput（errno 13）：请确认 uinput 模块已加载，且当前用户有写权限；"
                + "可执行 sudo usermod -aG input $USER 后重新登录");

        vm.SyncControllers();

        Assert.Contains("Generic X-Box pad", vm.StatusMessage);
        Assert.Contains("注入不可用", vm.StatusMessage);
        Assert.Contains("/dev/uinput", vm.StatusMessage);
        Assert.Contains("usermod -aG input", vm.StatusMessage);
    }

    [Fact]
    public void The_Sink_Error_Is_Quoted_Once_When_No_Pad_Is_Connected()
    {
        var vm = CreateView(
            runtimeHints: static () => new LinuxRuntimeHints(null, "无法打开 /dev/uinput（errno 13）"),
            sinkError: "无法打开 /dev/uinput（errno 13）：请确认 uinput 模块已加载，且当前用户有写权限；"
                + "可执行 sudo usermod -aG input $USER 后重新登录");

        // The sink error is the session's actual state and covers the probe's open-check
        // wording, so the shorter probe text must not double up beside it.
        Assert.Contains("注入不可用", vm.StatusMessage);
        Assert.Contains("usermod -aG input", vm.StatusMessage);
        Assert.Equal(1, vm.StatusMessage.Split("注入不可用").Length - 1);
    }

    [Fact]
    public void A_Sink_Error_Survives_A_Probe_That_Throws()
    {
        var vm = CreateView(
            runtimeHints: static () => throw new IOException("probe failed"),
            sinkError: "ioctl 0x40045564 失败 (errno 22)。");

        Assert.Contains("注入不可用", vm.StatusMessage);
        Assert.Contains("ioctl 0x40045564", vm.StatusMessage);
    }

    [Fact]
    public void A_Connected_Controller_Keeps_Its_Device_Line_Without_Diagnostics()
    {
        var store = new InMemoryProfileStore();
        var provider = new FakeGamepadProvider();
        provider.Add("/dev/input/event3", "Generic X-Box pad");
        var host = new EngineHost(provider, new RecordingOutputSink(), store);
        var vm = new MainWindowViewModel(
            host,
            store,
            runtimeHints: static () => new LinuxRuntimeHints("权限不足（errno 13）", "无法打开 /dev/uinput"));

        vm.SyncControllers();

        Assert.Contains("Generic X-Box pad", vm.StatusMessage);
        Assert.DoesNotContain("usermod", vm.StatusMessage);
    }

    [Fact]
    public void A_Disconnected_Pad_Keeps_The_Sleep_Wording_Without_Diagnostics()
    {
        var store = new InMemoryProfileStore();
        var provider = new FakeGamepadProvider();
        var host = new EngineHost(provider, new RecordingOutputSink(), store);
        var vm = new MainWindowViewModel(
            host,
            store,
            runtimeHints: static () => new LinuxRuntimeHints("权限不足（errno 13）", null));

        provider.Add("/dev/input/event3", "Generic X-Box pad");
        vm.SyncControllers();
        provider.Remove("/dev/input/event3");
        vm.SyncControllers();

        Assert.Contains("已断开连接", vm.StatusMessage);
        Assert.DoesNotContain("usermod", vm.StatusMessage);
    }

    // With a pad connected the status line must also carry the unhandled-axis diagnostic: an
    // axis no mapping reaches is indistinguishable from a broken stick or trigger, and it used
    // to be invisible the moment a pad showed up. The hint rides the same suffix pattern the
    // sink error uses - appended, quoted once, and never costing the line a throwing probe.
    [Fact]
    public void A_Connected_Pad_Quotes_Its_Unhandled_Axes()
    {
        var vm = CreateViewWithPad(static () => new LinuxRuntimeHints(
            null,
            null,
            "未处理的轴（原始码/量程/当前值，供 evtest 对照）：/dev/input/event3 code 0x02 (2) 量程 0..255 当前 128"));

        Assert.Contains("Generic X-Box pad", vm.StatusMessage);
        Assert.Contains("未处理的轴", vm.StatusMessage);
        Assert.Contains("code 0x02", vm.StatusMessage);
        Assert.EndsWith("量程 0..255 当前 128", vm.StatusMessage);
    }

    [Fact]
    public void A_Pad_With_No_Unhandled_Axes_Leaves_The_Status_Line_Unchanged()
    {
        var vm = CreateViewWithPad(static () => new LinuxRuntimeHints(null, null));

        // Byte-for-byte the plain device line: no axis suffix when nothing is unmapped, and no
        // sink error is set here either.
        var expected = $"{vm.ActiveController!.Descriptor.Name} · " +
            $"{MappingDescriptions.Layout(vm.ActiveController.Descriptor.Layout)} · 映射未启动";

        Assert.Equal(expected, vm.StatusMessage);
    }

    [Fact]
    public void The_Unhandled_Axis_Hint_Coexists_With_The_Sink_Error_Without_Duplication()
    {
        var vm = CreateViewWithPad(
            static () => new LinuxRuntimeHints(
                null,
                null,
                "未处理的轴（原始码/量程/当前值，供 evtest 对照）：/dev/input/event3 code 0x02 (2) 量程 0..255 当前 128"),
            sinkError: "无法打开 /dev/uinput（errno 13）：请确认 uinput 模块已加载。");

        Assert.Contains("Generic X-Box pad", vm.StatusMessage);
        Assert.Contains("注入不可用", vm.StatusMessage);
        Assert.Contains("/dev/uinput", vm.StatusMessage);
        Assert.Contains("未处理的轴", vm.StatusMessage);
        // Each suffix is quoted exactly once - neither swallows nor doubles the other.
        Assert.Equal(1, vm.StatusMessage.Split("注入不可用").Length - 1);
        Assert.Equal(1, vm.StatusMessage.Split("未处理的轴").Length - 1);
    }

    [Fact]
    public void A_Probe_That_Throws_Keeps_The_Device_Line_And_The_Sink_Error()
    {
        var vm = CreateViewWithPad(
            static () => throw new IOException("probe failed"),
            sinkError: "ioctl 0x40045564 失败 (errno 22)。");

        Assert.Contains("Generic X-Box pad", vm.StatusMessage);
        Assert.Contains("注入不可用", vm.StatusMessage);
        Assert.Contains("ioctl 0x40045564", vm.StatusMessage);
        Assert.DoesNotContain("未处理的轴", vm.StatusMessage);
    }

    [Fact]
    public void A_Probe_That_Throws_Leaves_A_Clean_Pad_Line_Untouched()
    {
        var vm = CreateViewWithPad(static () => throw new IOException("probe failed"));

        var expected = $"{vm.ActiveController!.Descriptor.Name} · " +
            $"{MappingDescriptions.Layout(vm.ActiveController.Descriptor.Layout)} · 映射未启动";

        Assert.Equal(expected, vm.StatusMessage);
    }

    [Fact]
    public void The_No_Gamepad_Line_Does_Not_Gain_An_Unhandled_Axis_Hint()
    {
        // The axis half belongs to the pad-connected line; the "connect a gamepad" line is
        // unchanged, so a probe that somehow carries an axis hint does not leak into it.
        var vm = CreateView(static () => new LinuxRuntimeHints(
            null,
            null,
            "未处理的轴（原始码/量程/当前值，供 evtest 对照）：/dev/input/event3 code 0x02 (2) 量程 0..255 当前 128"));

        Assert.Equal("未检测到手柄，请连接手柄", vm.StatusMessage);
    }

    private static MainWindowViewModel CreateViewWithPad(
        Func<LinuxRuntimeHints> runtimeHints,
        string? sinkError = null)
    {
        var store = new InMemoryProfileStore();
        var provider = new FakeGamepadProvider();
        provider.Add("/dev/input/event3", "Generic X-Box pad");
        var host = new EngineHost(provider, new RecordingOutputSink(), store);

        var vm = new MainWindowViewModel(host, store, runtimeHints: runtimeHints, sinkError: sinkError);
        vm.SyncControllers();
        return vm;
    }
}
