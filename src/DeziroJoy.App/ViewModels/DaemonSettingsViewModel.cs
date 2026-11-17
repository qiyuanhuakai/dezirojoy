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
/// any platform and is unit-tested with a fake. On the same-user platform the card also drives
/// the one-click device-access fix through <see cref="IDeviceAccessFixer"/> - the Linux-only
/// permission repair mapping depends on - and publishes its outcome with its own status line.
/// <para>
/// The card also hosts the two switches that only a restart can apply: the daemon's input
/// backend (the provider is built at daemon startup) and, on Windows alone, the GUI's
/// experimental software rendering selection (the renderer is built at process startup). Both
/// persist through their own store - <see cref="IInputBackendStore"/> beside the profiles, the
/// shared <see cref="ThemeService"/> settings file - and neither ever claims the running
/// process already changed.
/// </para>
/// </summary>
public partial class DaemonSettingsViewModel : ObservableObject
{
    private readonly IDaemonService _service;
    private readonly IDeviceAccessFixer _deviceAccessFixer;
    private readonly IDeviceAccessProbe _deviceAccessProbe;
    private readonly IInputBackendStore _backendStore;
    private readonly ThemeService _settings;
    private readonly Func<bool> _isWindows;
    private readonly Func<Task<bool>>? _restartDaemon;
    private DeviceAccessSnapshot? _deviceAccess;
    private string? _deviceAccessProbeError;
    private bool _suppressAutostartChange;

    /// <summary>
    /// What the daemon last reported as its running backend, or null when no daemon has
    /// said. Kept beside the selection so the card can show the two apart: a saved
    /// selection the daemon has not adopted yet is a pending restart, not a running fact.
    /// </summary>
    private InputBackend? _runningBackend;

    /// <summary>Why the daemon's backend could not start, as the daemon itself reported it.</summary>
    private string? _runningBackendError;

    public DaemonSettingsViewModel(
        IDaemonService? service = null,
        IDeviceAccessFixer? deviceAccessFixer = null,
        IDeviceAccessProbe? deviceAccessProbe = null,
        IInputBackendStore? backendStore = null,
        Func<Task<bool>>? restartDaemon = null,
        ThemeService? settings = null,
        Func<bool>? isWindows = null)
    {
        _service = service ?? PlatformFactory.CreateDaemonService();
        _deviceAccessFixer = deviceAccessFixer ?? new DeviceAccessFixer();
        _deviceAccessProbe = deviceAccessProbe ?? new LinuxDeviceAccessProbe();
        _backendStore = backendStore ?? new InputBackendStore();
        _settings = settings ?? new ThemeService();
        _isWindows = isWindows ?? (static () => OperatingSystem.IsWindows());
        _restartDaemon = restartDaemon;
        _selectedBackend = _backendStore.Load();
        if (_backendStore.LastError is { } backendReadError)
        {
            // A damaged file reads as the default backend; the card says so on its own
            // status line instead of silently showing a selection nobody chose.
            BackendStatusMessage = backendReadError;
        }

        _selectedRenderMode = _settings.RenderMode;

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
    /// Whether the installed unit starts the daemon at logon (systemd's "enabled"). A separate
    /// fact from <see cref="IsDaemonRunning"/> - enabled and stopped, or running and disabled,
    /// are both real states - which is why it gets its own line instead of being folded into
    /// the run state.
    /// </summary>
    public bool IsDaemonEnabled => _service.IsDaemonEnabled;

    /// <summary>Why the last refresh could not read the daemon state, or null when it could.</summary>
    public string? DaemonQueryError => _service.DaemonQueryError;

    /// <summary>
    /// Why the installed unit's enablement needs a second look - the scheduler cannot see the
    /// unit file at all, or the unit can never auto-start (static/masked) - or null when the
    /// plain enabled/disabled line says everything. This is the reason behind a 未启用 the user
    /// did not expect: the file was written where systemd does not search, so nothing is
    /// enabled and nothing will start no matter how often the card is refreshed.
    /// </summary>
    public string? DaemonEnablementNote => _service.DaemonEnablementNote;

    public bool HasDaemonEnablementNote => DaemonEnablementNote is not null;

    /// <summary>
    /// The unit's login-autostart state as its own line: "已启用（登录自动）" once the unit
    /// exists on the same-user platform, "未启用（登录不会自动启动）" when it will not start at
    /// login, and null everywhere else - a Windows scheduled task runs at logon once installed,
    /// a unit that was never installed has no enablement to show, and a failed query proves
    /// nothing about enablement either.
    /// </summary>
    public string? DaemonEnabledText => IsSameUserModel && IsDaemonInstalled && DaemonQueryError is null
        ? IsDaemonEnabled ? "已启用（登录自动）" : "未启用（登录不会自动启动）"
        : null;

    public bool HasDaemonEnabledText => DaemonEnabledText is not null;

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
                // Linux: the unit file is the install, systemd's own states are the run and
                // enablement states, and a query that never answered is a state of its own.
                // The user's "每次刷新都显示未启用" was a bus the GUI could not reach being
                // reported as a run state on every refresh.
                if (!IsDaemonInstalled)
                {
                    return "常驻服务：未安装";
                }

                if (DaemonQueryError is { } error)
                {
                    return $"常驻服务：已安装，但无法查询状态：{error}";
                }

                return IsDaemonRunning
                    ? "常驻服务：已安装并正在运行"
                    : "常驻服务：已安装但未运行";
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
                return "同用户运行，无需管理员权限；设备访问状态见下方检测。";
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

    public string DeviceAccessStatusText
    {
        get
        {
            if (_deviceAccessProbeError is not null)
            {
                return $"设备权限：检测失败（{_deviceAccessProbeError}）";
            }

            if (_deviceAccess is not { } status)
            {
                return "设备权限：尚未检测";
            }

            var gaps = new List<string>();
            if (status.DeniedNodeCount > 0)
            {
                gaps.Add($"{status.DeniedNodeCount} 个输入节点不可读");
            }

            if (status.UInputUnavailableReason is not null)
            {
                gaps.Add("uinput 不可用");
            }

            if (gaps.Count > 0)
            {
                return "设备权限：" + string.Join("；", gaps);
            }

            return status.GamepadCount > 0
                ? "设备权限：正常（手柄可读，uinput 可写）"
                : "设备权限：未发现缺口；当前未检测到手柄";
        }
    }

    public string DeviceAccessConfigurationText => _deviceAccess is not { } status
        ? string.Empty
        : $"持久配置：udev 规则{(status.UdevRulesInstalled ? "已安装" : "未安装")}；"
            + (status.UserInInputGroup
                ? status.SessionInInputGroup ? "input 组已生效" : "input 组需重新登录后生效"
                : "未加入 input 组");

    public string DeviceAccessFixButtonText => _deviceAccess?.CanFixAutomatically == true
        ? "自动修复权限"
        : "查看手动修复方法";

    public string DeviceAccessMethodText => _deviceAccess?.CanFixAutomatically == true
        ? "自动修复需要一次管理员授权。"
        : "缺少 pkexec 或修复脚本时，按钮会给出可复制的手动命令。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>
    /// The one-click device-access fix is a Linux-only affordance: evdev/uinput permissions
    /// are the platform's own model (the same reason the card swaps its wording on the
    /// same-user platform), so the card offers the button nowhere else.
    /// </summary>
    public bool CanFixDeviceAccess => IsSameUserModel && !IsFixingDeviceAccess;

    /// <summary>True while the fix runs; the button is disabled against it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFixDeviceAccess))]
    private bool _isFixingDeviceAccess;

    /// <summary>
    /// The fix's own status line - fixing / fixed / degraded / failed / nothing to fix - kept
    /// separate from <see cref="StatusMessage"/> so a daemon operation can never overwrite the
    /// outcome the user just asked for.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceAccessFixStatus))]
    private string _deviceAccessFixStatus = string.Empty;

    public bool HasDeviceAccessFixStatus => DeviceAccessFixStatus.Length > 0;

    /// <summary>
    /// Why the last fix degraded or failed, or empty. The button's tooltip always states what
    /// the fix changes; after a degraded run it also carries the reason, so a machine without
    /// a polkit agent gets an explanation instead of a button that silently does nothing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceAccessFixTooltip))]
    private string _deviceAccessFixNote = string.Empty;

    /// <summary>
    /// The button's tooltip: what the fix will change (rules file + input group + re-login),
    /// plus the last degradation reason where there is one.
    /// </summary>
    public string DeviceAccessFixTooltip => DeviceAccessFixNote.Length > 0
        ? $"{LinuxDeviceAccess.FixNotice}\n上次未使用一键修复的原因：{DeviceAccessFixNote}"
        : LinuxDeviceAccess.FixNotice;

    [ObservableProperty]
    private bool _autostartEnabled;

    /// <summary>
    /// The persisted input-backend selection. SDL3 is the default since S3; the legacy
    /// XInput/evdev backend is the explicit fallback. A change here is only a request until
    /// <see cref="ApplyBackend"/> persists it and the daemon restarts onto it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLegacyBackendSelected))]
    [NotifyPropertyChangedFor(nameof(IsSdl3BackendSelected))]
    [NotifyPropertyChangedFor(nameof(HasBackendMismatch))]
    [NotifyPropertyChangedFor(nameof(BackendMismatchText))]
    private InputBackend _selectedBackend;

    /// <summary>True while the radio for the legacy fallback backend must read as checked.</summary>
    public bool IsLegacyBackendSelected
    {
        get => SelectedBackend == InputBackend.Legacy;
        set
        {
            if (value)
            {
                SelectedBackend = InputBackend.Legacy;
            }
        }
    }

    /// <summary>True while the radio for the SDL3 backend must read as checked.</summary>
    public bool IsSdl3BackendSelected
    {
        get => SelectedBackend == InputBackend.Sdl3;
        set
        {
            if (value)
            {
                SelectedBackend = InputBackend.Sdl3;
            }
        }
    }

    /// <summary>The backend the daemon last reported, or null when none has.</summary>
    public string RunningBackendText => _runningBackend is { } backend
        ? $"运行中的后端：{PlatformFactory.DescribeBackend(backend)}"
        : "运行中的后端：未知（尚未连接常驻服务）";

    /// <summary>
    /// Why the daemon's backend could not start, as the daemon reported it. A selection that
    /// failed is shown here beside the running backend - never silently swapped for another
    /// one - so the user sees which backend is actually in charge and why.
    /// </summary>
    public string? RunningBackendErrorText => _runningBackendError is { } error
        ? $"后端启动失败：{error}（未切换回其他后端，当前不读取手柄输入）"
        : null;

    public bool HasRunningBackendError => RunningBackendErrorText is not null;

    /// <summary>
    /// True when the saved selection and the running backend are known to differ - the state
    /// between saving and the daemon actually restarting onto the new backend.
    /// </summary>
    public bool HasBackendMismatch => _runningBackend is { } backend && backend != SelectedBackend;

    public string BackendMismatchText => HasBackendMismatch
        ? $"设置已改为 {PlatformFactory.DescribeBackend(SelectedBackend)}，重启常驻服务后生效。"
        : string.Empty;

    /// <summary>The outcome of the last apply, kept on its own line so it reads as a result.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackendStatusMessage))]
    private string _backendStatusMessage = string.Empty;

    public bool HasBackendStatusMessage => BackendStatusMessage.Length > 0;

    /// <summary>
    /// Records what the daemon reported about the backend it is running. Called from every
    /// snapshot, so a restart onto the new selection - or a failed start that kept the
    /// daemon on no provider at all - reaches the card without a manual refresh.
    /// </summary>
    public void ReportRunningBackend(InputBackend? backend, string? error)
    {
        _runningBackend = backend;
        _runningBackendError = error;
        OnPropertyChanged(nameof(RunningBackendText));
        OnPropertyChanged(nameof(RunningBackendErrorText));
        OnPropertyChanged(nameof(HasRunningBackendError));
        OnPropertyChanged(nameof(HasBackendMismatch));
        OnPropertyChanged(nameof(BackendMismatchText));
    }

    /// <summary>
    /// Persists the selection and asks for the safe restart that makes it take effect. The
    /// daemon builds its provider at startup and there is no hot swap, so the restart is the
    /// only honest way to apply a switch: the daemon stops gracefully - releasing every held
    /// key - and the replacement reads the saved selection.
    /// <para>
    /// The status line never claims the new backend is running: it says the restart was
    /// requested, and the running-backend line above - fed by the daemon's own snapshots -
    /// is what tells the user which backend is actually in charge, including a start that
    /// failed on the new selection.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task ApplyBackend()
    {
        var requested = SelectedBackend;
        _backendStore.Save(requested);

        if (_restartDaemon is null)
        {
            // No daemon to restart here (a card constructed without the seam): the selection
            // is persisted, and the line says what still has to happen instead of implying
            // it already did.
            BackendStatusMessage = "已保存输入后端设置；重启常驻服务后生效。";
            return;
        }

        BackendStatusMessage = "正在安全重启常驻服务以应用新的输入后端…";

        bool restarted;
        try
        {
            restarted = await _restartDaemon();
        }
        catch (Exception exception)
        {
            restarted = false;
            BackendStatusMessage = $"重启常驻服务时出错：{exception.Message}。设置已保存，请手动重启常驻服务。";
            return;
        }

        BackendStatusMessage = restarted
            ? "已保存并重启常驻服务；上方“运行中的后端”显示守护进程实际使用的后端。"
            : "已保存设置，但重启常驻服务失败：请手动重启。重启前守护进程仍在原后端上运行，不会静默切换。";
    }

    /// <summary>
    /// The persisted rendering selection. <see cref="AppRenderMode.Default"/> is the platform's
    /// own detection and what a settings file that never chose reads back as;
    /// <see cref="AppRenderMode.Software"/> asks the Win32 backend for CPU rendering only. A
    /// change here is a request until <see cref="ApplyRenderMode"/> persists it and the next
    /// GUI start honors it - there is no live renderer swap, so the card never implies the
    /// running window changed how it draws.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefaultRenderSelected))]
    [NotifyPropertyChangedFor(nameof(IsSoftwareRenderSelected))]
    private AppRenderMode _selectedRenderMode;

    /// <summary>True while the radio for platform detection must read as checked.</summary>
    public bool IsDefaultRenderSelected
    {
        get => SelectedRenderMode == AppRenderMode.Default;
        set
        {
            if (value)
            {
                SelectedRenderMode = AppRenderMode.Default;
            }
        }
    }

    /// <summary>True while the radio for the software renderer must read as checked.</summary>
    public bool IsSoftwareRenderSelected
    {
        get => SelectedRenderMode == AppRenderMode.Software;
        set
        {
            if (value)
            {
                SelectedRenderMode = AppRenderMode.Software;
            }
        }
    }

    /// <summary>
    /// The software mode exists on the Win32 backend alone, so the whole section is offered
    /// only on Windows. Elsewhere the platform's own detection runs no matter what a settings
    /// file says, and offering a switch that cannot act would be a lie.
    /// </summary>
    public bool IsRenderModeVisible => _isWindows();

    /// <summary>The outcome of the last apply, kept on its own line so it reads as a result.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenderModeStatusMessage))]
    private string _renderModeStatusMessage = string.Empty;

    public bool HasRenderModeStatusMessage => RenderModeStatusMessage.Length > 0;

    /// <summary>
    /// Persists the selection and says what still has to happen for it to matter. The
    /// renderer is built once per process and cannot be swapped live, so the restart is the
    /// only honest way to apply a switch - and the daemon is deliberately not restarted: it
    /// draws nothing, and bouncing it would cost the user their mappings for no reason. No
    /// claim is made about what the new mode does better; the line only states what was
    /// saved and when it takes effect.
    /// </summary>
    [RelayCommand]
    private void ApplyRenderMode()
    {
        _settings.SetRenderMode(SelectedRenderMode);
        RenderModeStatusMessage = "已保存渲染设置；重启主程序后生效（当前窗口仍按启动时的渲染方式运行）。";
    }

    /// <summary>Re-reads the scheduler and token, then republishes every derived property.</summary>
    public void RefreshFromService()
    {
        _service.Refresh();
        RefreshDeviceAccess();

        _suppressAutostartChange = true;
        AutostartEnabled = _service.IsAutostartInstalled;
        _suppressAutostartChange = false;

        OnPropertyChanged(nameof(IsSupported));
        OnPropertyChanged(nameof(IsSameUserModel));
        OnPropertyChanged(nameof(IsElevated));
        OnPropertyChanged(nameof(IsDaemonInstalled));
        OnPropertyChanged(nameof(IsDaemonRunning));
        OnPropertyChanged(nameof(IsDaemonEnabled));
        OnPropertyChanged(nameof(DaemonQueryError));
        OnPropertyChanged(nameof(DaemonEnablementNote));
        OnPropertyChanged(nameof(HasDaemonEnablementNote));
        OnPropertyChanged(nameof(DaemonEnabledText));
        OnPropertyChanged(nameof(HasDaemonEnabledText));
        OnPropertyChanged(nameof(CanStartDaemon));
        OnPropertyChanged(nameof(CanStopDaemon));
        OnPropertyChanged(nameof(DaemonRunLevel));
        OnPropertyChanged(nameof(DaemonStateText));
        OnPropertyChanged(nameof(ElevationText));
        OnPropertyChanged(nameof(CanFixDeviceAccess));
        OnPropertyChanged(nameof(DeviceAccessFixTooltip));
    }

    private void RefreshDeviceAccess()
    {
        if (!IsSameUserModel)
        {
            return;
        }

        try
        {
            _deviceAccess = _deviceAccessProbe.Probe();
            _deviceAccessProbeError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _deviceAccess = null;
            _deviceAccessProbeError = exception.Message;
        }

        OnPropertyChanged(nameof(DeviceAccessStatusText));
        OnPropertyChanged(nameof(DeviceAccessConfigurationText));
        OnPropertyChanged(nameof(DeviceAccessFixButtonText));
        OnPropertyChanged(nameof(DeviceAccessMethodText));
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

    /// <summary>
    /// The one-click device-access fix. pkexec blocks on the polkit authentication dialog,
    /// which can take as long as the user takes to answer it, so the work runs off the UI
    /// thread and the card reports the fixing state while it does. Every outcome is published
    /// verbatim from <see cref="IDeviceAccessFixer"/>: fixed, degraded (with the manual
    /// commands and the reason), failed, or nothing to fix.
    /// </summary>
    [RelayCommand]
    private async Task FixDeviceAccess()
    {
        if (IsFixingDeviceAccess)
        {
            return;
        }

        IsFixingDeviceAccess = true;
        DeviceAccessFixStatus = "正在修复设备访问权限…";

        try
        {
            var result = await Task.Run(() => _deviceAccessFixer.Fix());

            DeviceAccessFixStatus = result.Message;
            DeviceAccessFixNote = result.Reason ?? string.Empty;
            RefreshDeviceAccess();
        }
        catch (Exception exception)
        {
            // The fixer is written not to throw; a crash here must still leave the card
            // usable and say what happened.
            DeviceAccessFixStatus = $"修复设备访问权限失败：{exception.Message}";
            DeviceAccessFixNote = string.Empty;
        }
        finally
        {
            IsFixingDeviceAccess = false;
        }
    }

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
