using System.Diagnostics;
using Microsoft.Win32;

namespace DeziroJoy.Platform.Windows.Gaming;

/// <summary>Controls Windows' per-user "open Game Bar with the controller" setting.</summary>
public interface IGameBarPopupGuard
{
    bool IsSupported { get; }

    bool IsControllerLaunchDisabled { get; }

    void DisableControllerLaunch();

    void OpenSystemSettings();
}

/// <summary>
/// Writes and verifies the current user's controller-launch setting and offers the matching
/// Windows settings page. This does not disable Win+G or register a protocol handler.
/// </summary>
public sealed class GameBarPopupGuard : IGameBarPopupGuard
{
    private const string KeyPath = @"Software\Microsoft\GameBar";
    private const string ValueName = "UseNexusForGameBarEnabled";

    private readonly Func<bool> _isSupported;
    private readonly Func<int?> _readValue;
    private readonly Action _disable;

    public GameBarPopupGuard()
        : this(static () => OperatingSystem.IsWindows(), ReadRegistryValue, DisableRegistryValue)
    {
    }

    internal GameBarPopupGuard(Func<bool> isSupported, Func<int?> readValue, Action disable)
    {
        _isSupported = isSupported;
        _readValue = readValue;
        _disable = disable;
    }

    public bool IsSupported => _isSupported();

    public bool IsControllerLaunchDisabled => IsSupported && _readValue() == 0;

    public void DisableControllerLaunch()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Game Bar controller launch is a Windows setting.");
        }

        _disable();
        if (_readValue() != 0)
        {
            throw new InvalidOperationException("注册表写入后仍未读到关闭状态，请改用 Windows 系统设置。");
        }
    }

    public void OpenSystemSettings()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Game Bar settings are available only on Windows.");
        }

        Process.Start(new ProcessStartInfo("ms-settings:gaming-gamebar") { UseShellExecute = true });
    }

    private static int? ReadRegistryValue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) is int value ? value : null;
    }

    private static void DisableRegistryValue()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open the current user's Game Bar settings.");
        key.SetValue(ValueName, 0, RegistryValueKind.DWord);
    }
}
