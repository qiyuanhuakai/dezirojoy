using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.App.Services;
using DeziroJoy.Platform.Linux;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// Sidebar binding for the resident-daemon and logon-autostart settings. All state is read from
/// the real <see cref="IDaemonService"/> (schtasks + process token on Windows, a systemd user
/// unit on Linux); nothing is fabricated. The service defaults to the platform implementation
/// through <see cref="PlatformFactory.CreateDaemonService"/>, so the card is safe to construct on
/// any platform and is unit-tested with a fake.
/// </summary>
public partial class DaemonSettingsViewModel : ObservableObject
{
    private readonly IDaemonService _service;
    private bool _suppressAutostartChange;

    public DaemonSettingsViewModel(IDaemonService? service = null)
    {
        _service = service ?? PlatformFactory.CreateDaemonService();
        RefreshFromService();
    }

    public bool IsSupported => _service.IsSupported;

    /// <summary>
    /// True where the daemon runs as the current user and elevation changes nothing by design
    /// (Linux: uinput is a kernel interface UIPI does not gate). The card then swaps the Windows
    /// UIPI wording for the platform's own explanation instead of claiming "not applicable".
    /// </summary>
    public bool IsSameUserModel => _service.IsSameUserModel;

    public bool IsElevated => _service.IsElevated;

    public bool IsDaemonInstalled => _service.IsDaemonInstalled;

    /// <summary>Whether the resident daemon is running right now; only reported where known.</summary>
    public bool IsDaemonRunning => _service.IsDaemonRunning;

    /// <summary>
    /// The start button is operable only on the same-user platform (Linux), with the unit
    /// installed and not yet running; the stop button is its exact mirror. Windows keeps
    /// neither - a scheduled task has no standalone start - so both are false there.
    /// </summary>
    public bool CanStartDaemon => IsSameUserModel && IsDaemonInstalled && !IsDaemonRunning;

    public bool CanStopDaemon => IsSameUserModel && IsDaemonInstalled && IsDaemonRunning;

    public string? DaemonRunLevel => _service.DaemonRunLevel;

    public string DaemonStateText
    {
        get
        {
            if (!IsSupported)
            {
                return "常驻服务：当前平台不支持";
            }

            if (IsSameUserModel)
            {
                // Linux: the unit file is the install and systemd's own state is the run
                // state, so "installed" and "running" are two different facts here.
                return IsDaemonInstalled
                    ? IsDaemonRunning
                        ? "常驻服务：已安装并正在运行（systemd 用户单元）"
                        : "常驻服务：已安装但未运行（systemd 用户单元）"
                    : "常驻服务：未安装（systemd 用户单元）";
            }

            return IsDaemonInstalled
                ? "常驻服务：已安装"
                : "常驻服务：未安装";
        }
    }

    /// <summary>
    /// Injection reach is decided by the daemon's run level, not this window's token: the GUI
    /// deliberately stays unelevated and only configures.
    /// </summary>
    public string ElevationText
    {
        get
        {
            if (!IsSupported)
            {
                return "注入权限：不适用";
            }

            if (IsSameUserModel)
            {
                return
                    "注入权限：Linux 为同用户设计——uinput 是内核级接口，不受 Windows UIPI 约束，"
                    + "守护进程以当前用户身份运行，无需提权。注入只取决于设备访问权："
                    + LinuxDeviceAccess.DeviceAccessGuidance;
            }

            if (!IsDaemonInstalled)
            {
                return "注入权限：常驻服务未安装，无法向以管理员身份运行的应用注入输入（UIPI 会静默拦截）。安装常驻服务即会提权。";
            }

            if (string.Equals(DaemonRunLevel, "Highest", StringComparison.OrdinalIgnoreCase))
            {
                return "注入权限：常驻服务以管理员身份运行，可向管理员权限的应用注入输入";
            }

            if (string.Equals(DaemonRunLevel, "LeastPrivilege", StringComparison.OrdinalIgnoreCase))
            {
                return "注入权限：常驻服务未以管理员身份运行，无法向管理员权限的应用注入输入";
            }

            return "注入权限：常驻服务已安装，但无法读取其运行级别（非管理员可能被拒绝读取管理员创建的任务）";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    [ObservableProperty]
    private bool _autostartEnabled;

    /// <summary>Re-reads the scheduler and token, then republishes every derived property.</summary>
    public void RefreshFromService()
    {
        _service.Refresh();

        _suppressAutostartChange = true;
        AutostartEnabled = _service.IsAutostartInstalled;
        _suppressAutostartChange = false;

        OnPropertyChanged(nameof(IsSupported));
        OnPropertyChanged(nameof(IsSameUserModel));
        OnPropertyChanged(nameof(IsElevated));
        OnPropertyChanged(nameof(IsDaemonInstalled));
        OnPropertyChanged(nameof(IsDaemonRunning));
        OnPropertyChanged(nameof(CanStartDaemon));
        OnPropertyChanged(nameof(CanStopDaemon));
        OnPropertyChanged(nameof(DaemonRunLevel));
        OnPropertyChanged(nameof(DaemonStateText));
        OnPropertyChanged(nameof(ElevationText));
    }

    partial void OnAutostartEnabledChanged(bool value)
    {
        if (_suppressAutostartChange)
        {
            return;
        }

        ApplyResult(_service.SetAutostart(value));
    }

    [RelayCommand]
    private void InstallDaemon() => ApplyResult(_service.InstallDaemon());

    [RelayCommand]
    private void UninstallDaemon() => ApplyResult(_service.UninstallDaemon());

    [RelayCommand]
    private void StartDaemon() => ApplyResult(_service.StartDaemon());

    [RelayCommand]
    private void StopDaemon() => ApplyResult(_service.StopDaemon());

    [RelayCommand]
    private void Refresh() => RefreshFromService();

    private void ApplyResult(DaemonActionResult result)
    {
        StatusMessage = result.Message;

        // A pending elevated relaunch has not changed the task yet, so the toggle keeps the
        // requested value and the user refreshes once the elevated copy finishes.
        if (result.Result != DaemonOp.Pending)
        {
            RefreshFromService();
        }
    }
}
