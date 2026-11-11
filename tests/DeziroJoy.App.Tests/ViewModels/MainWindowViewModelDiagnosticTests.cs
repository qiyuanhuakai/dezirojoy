using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Platform.Linux;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The status area must explain an empty device table, not just announce it. "Connect a
/// gamepad" is useless advice when the nodes are unreadable or absent, so the probe's wording
/// is quoted - and a probe that has nothing to say leaves the original line untouched.
/// </summary>
public class MainWindowViewModelDiagnosticTests
{
    private static MainWindowViewModel CreateView(
        Func<LinuxRuntimeHints>? runtimeHints = null,
        bool daemonRequired = false)
    {
        var store = new InMemoryProfileStore();
        var host = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);

        return new MainWindowViewModel(
            host,
            store,
            daemonRequired: daemonRequired,
            runtimeHints: runtimeHints);
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
}
