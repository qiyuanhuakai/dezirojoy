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

    public string DaemonStateText => !IsSupported
        ? "常驻服务：当前平台不支持"
        : IsDaemonInstalled
            ? "常驻服务：已安装"
            : "常驻服务：未安装";

    public string ElevationText => !IsSupported
        ? "提权状态：不适用"
        : IsElevated
            ? "提权状态：已以管理员身份运行"
            : "提权状态：未提权（安装 / 卸载常驻需要管理员权限）";

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
