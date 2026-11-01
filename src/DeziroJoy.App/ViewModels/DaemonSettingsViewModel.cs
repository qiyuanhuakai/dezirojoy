using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.App.Services;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// Sidebar binding for the resident-daemon and logon-autostart settings. All state is read from
/// the real <see cref="IDaemonService"/> (schtasks + process token); nothing is fabricated. The
/// service defaults to the Windows implementation and degrades to "unsupported" elsewhere, so the
/// card is safe to construct on any platform and is unit-tested with a fake.
/// </summary>
public partial class DaemonSettingsViewModel : ObservableObject
{
    private readonly IDaemonService _service;
    private bool _suppressAutostartChange;

    public DaemonSettingsViewModel(IDaemonService? service = null)
    {
        _service = service ?? new WindowsDaemonService();
        RefreshFromService();
    }

    public bool IsSupported => _service.IsSupported;

    public bool IsElevated => _service.IsElevated;

    public bool IsDaemonInstalled => _service.IsDaemonInstalled;

    public string? DaemonRunLevel => _service.DaemonRunLevel;

    public string DaemonStateText => !IsSupported
        ? "常驻服务：当前平台不支持"
        : IsDaemonInstalled
            ? "常驻服务：已安装"
            : "常驻服务：未安装";

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

    /// <summary>Re-reads the task scheduler and token, then republishes every derived property.</summary>
    public void RefreshFromService()
    {
        _service.Refresh();

        _suppressAutostartChange = true;
        AutostartEnabled = _service.IsAutostartInstalled;
        _suppressAutostartChange = false;

        OnPropertyChanged(nameof(IsSupported));
        OnPropertyChanged(nameof(IsElevated));
        OnPropertyChanged(nameof(IsDaemonInstalled));
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
