using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.App.Services;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// Sidebar binding for the update card: the running version, a manual check with its result,
/// and the persisted startup auto-check. The check itself lives in <see cref="IUpdateService"/>
/// (network, semver, Chinese error wording), so this view model stays Avalonia-free and
/// testable; the preference is persisted through the shared <see cref="ThemeService"/>, the
/// same store every other app preference uses. Opening the release page is the one platform
/// action the view model deliberately does not perform - it raises
/// <see cref="OpenReleasePageRequested"/> and the view answers it.
/// </summary>
public partial class UpdateSettingsViewModel : ObservableObject
{
    private readonly IUpdateService _service;

    private readonly ThemeService? _settings;

    private bool _suppressAutoCheckChange;

    public UpdateSettingsViewModel(IUpdateService service, ThemeService? settings = null)
    {
        ArgumentNullException.ThrowIfNull(service);

        _service = service;
        _settings = settings;
        _currentVersionText = service.CurrentVersionText;

        _suppressAutoCheckChange = true;
        _autoCheckEnabled = settings?.AutoCheckUpdatesEnabled ?? false;
        _suppressAutoCheckChange = false;
    }

    /// <summary>The running version, e.g. "0.0.1-beta.3-preview".</summary>
    [ObservableProperty]
    private string _currentVersionText;

    /// <summary>
    /// The outcome line: "正在检查更新…", "发现新版本：…（发布于 …）", "已是最新版本。" or a
    /// Chinese failure reason.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _resultText = string.Empty;

    /// <summary>True when <see cref="ResultText"/> reports a failure rather than an outcome.</summary>
    [ObservableProperty]
    private bool _isResultError;

    /// <summary>True while a check is in flight; the manual button is disabled against it.</summary>
    [ObservableProperty]
    private bool _isChecking;

    /// <summary>True when the check found a newer release; drives the download button.</summary>
    [ObservableProperty]
    private bool _hasUpdate;

    /// <summary>The release page of the newest release, for the download button.</summary>
    [ObservableProperty]
    private string _releasePageUrl = string.Empty;

    /// <summary>Whether a release check runs once at startup; persisted.</summary>
    [ObservableProperty]
    private bool _autoCheckEnabled;

    public bool HasResult => ResultText.Length > 0;

    /// <summary>Raised when the user asks for the release page; the view owns opening it.</summary>
    public event EventHandler? OpenReleasePageRequested;

    [RelayCommand]
    private async Task CheckForUpdateAsync() => await CheckForUpdatesAsync(CancellationToken.None);

    [RelayCommand]
    private void OpenReleasePage() => OpenReleasePageRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Runs the check and publishes its outcome. The manual button and the startup auto-check
    /// both go through here, so the two can never drift apart. Cancellation (window teardown)
    /// says nothing: the panel keeps whatever it had.
    /// </summary>
    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        ResultText = "正在检查更新…";
        IsResultError = false;
        HasUpdate = false;

        try
        {
            ApplyResult(await _service.CheckForUpdateAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            ResultText = string.Empty;
        }
        catch (Exception exception)
        {
            ResultText = $"检查更新失败：{exception.Message}";
            IsResultError = true;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void ApplyResult(UpdateCheckResult result)
    {
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable when result.Latest is { } latest:
                var published = latest.PublishedAt is { } at
                    ? at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : "日期未知";
                ResultText = $"发现新版本：{latest.TagName}（发布于 {published}）";
                ReleasePageUrl = latest.PageUrl;
                HasUpdate = true;
                IsResultError = false;
                break;

            case UpdateCheckStatus.UpToDate:
                ResultText = "已是最新版本。";
                IsResultError = false;
                break;

            default:
                ResultText = result.ErrorMessage ?? "检查更新失败。";
                IsResultError = true;
                break;
        }
    }

    partial void OnAutoCheckEnabledChanged(bool value)
    {
        if (_suppressAutoCheckChange)
        {
            return;
        }

        _settings?.SetAutoCheckUpdates(value);
    }
}
