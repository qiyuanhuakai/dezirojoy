using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DeziroJoy.Platform.Windows.Gaming;

/// <summary>
/// The slice of the per-user registry <see cref="GameBarPopupGuard"/> touches, as a
/// key/value map rooted at <c>HKEY_CURRENT_USER</c>. The production implementation wraps
/// <see cref="Registry"/> and only works on Windows; tests run everywhere against an
/// in-memory map, so no test needs a real registry or a real machine.
/// </summary>
internal interface IGameBarRegistry
{
    int? ReadDWord(string keyPath, string? valueName);

    string? ReadString(string keyPath, string? valueName);

    void WriteDWord(string keyPath, string? valueName, int value);

    void WriteString(string keyPath, string? valueName, string value);

    void DeleteValue(string keyPath, string? valueName);

    void DeleteSubKeyTree(string keyPath, string subKeyPath);

    bool SubKeyExists(string keyPath, string subKeyPath);

    bool IsEmptyKey(string keyPath);

    void DeleteKey(string keyPath);
}

/// <summary>
/// The real registry view. Every operation is a no-op on a missing key except the writes,
/// which create the key they are given - matching what <see cref="Registry"/> does. Each
/// entry point guards the platform the same way the rest of the codebase does, so the
/// analyzer sees the call sites as legal everywhere.
/// </summary>
internal sealed class WindowsGameBarRegistry : IGameBarRegistry
{
    public int? ReadDWord(string keyPath, string? valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The registry only exists on Windows.");
        }

        return Open(keyPath)?.GetValue(valueName) is int value ? value : null;
    }

    public string? ReadString(string keyPath, string? valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The registry only exists on Windows.");
        }

        return Open(keyPath)?.GetValue(valueName) as string;
    }

    public void WriteDWord(string keyPath, string? valueName, int value)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The registry only exists on Windows.");
        }

        Writable(keyPath).SetValue(valueName, value, RegistryValueKind.DWord);
    }

    public void WriteString(string keyPath, string? valueName, string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The registry only exists on Windows.");
        }

        Writable(keyPath).SetValue(valueName, value, RegistryValueKind.String);
    }

    public void DeleteValue(string keyPath, string? valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The registry only exists on Windows.");
        }

        Open(keyPath, writable: true)?.DeleteValue(
            valueName ?? string.Empty,
            throwOnMissingValue: false);
    }

    public void DeleteSubKeyTree(string keyPath, string subKeyPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The registry only exists on Windows.");
        }

        Registry.CurrentUser.DeleteSubKeyTree(
            keyPath + "\\" + subKeyPath,
            throwOnMissingSubKey: false);
    }

    public bool SubKeyExists(string keyPath, string subKeyPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The registry only exists on Windows.");
        }

        return Open(keyPath + "\\" + subKeyPath) is not null;
    }

    public bool IsEmptyKey(string keyPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The registry only exists on Windows.");
        }

        return Open(keyPath) is { } key && key.ValueCount == 0 && key.SubKeyCount == 0;
    }

    public void DeleteKey(string keyPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The registry only exists on Windows.");
        }

        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
    }

    [SupportedOSPlatform("windows")]
    private static RegistryKey? Open(string keyPath, bool writable = false)
        => Registry.CurrentUser.OpenSubKey(keyPath, writable);

    [SupportedOSPlatform("windows")]
    private static RegistryKey Writable(string keyPath)
        => Registry.CurrentUser.CreateSubKey(keyPath)
            ?? throw new InvalidOperationException($"无法打开注册表项 {keyPath}。");
}

/// <summary>
/// Two independent layers against the Windows controller/Game Bar popup, both per-user and
/// both reversible. Layer one is the documented preference that stops the controller from
/// launching Game Bar; layer two is the no-op <see cref="GameBarProtocol.Name"/> handler
/// that answers the link when the preference alone does not - which is the case on systems
/// where Game Bar is missing and the shell finds no handler at all. Neither layer disables
/// a service or changes the system's recording settings; layer two only answers
/// <c>ms-gamebar:</c> links and never intercepts any other way of launching Game Bar.
/// </summary>
public interface IGameBarPopupGuard
{
    bool IsSupported { get; }

    /// <summary>Layer one: the per-user "controller may open Game Bar" preference is off.</summary>
    bool IsControllerLaunchDisabled { get; }

    void DisableControllerLaunch();

    /// <summary>
    /// Layer two: this app's no-op handler is registered, still owned by this app, and
    /// points at the running executable. False when nothing is registered, when another
    /// owner holds the protocol, or when the recorded command names a different path -
    /// an update that moved the executable leaves the registration stale.
    /// </summary>
    bool IsProtocolHandlerRegistered { get; }

    /// <summary>
    /// Layer two ownership: true when the marker is present AND the command is still one of
    /// this app's no-op handlers. The exe path may be stale - a moved or updated app - and
    /// it still counts as ours. A foreign command under a leftover marker is a takeover,
    /// not ownership: another program replaced the handler and never learned about our
    /// private marker, so acting on the marker alone would delete or overwrite THEIR
    /// registration.
    /// </summary>
    bool OwnsProtocolHandler { get; }

    /// <summary>
    /// The marker is present but the command is no longer this app's: another program took
    /// the protocol over. Neither restoring nor re-registering may touch it from the UI;
    /// the state is reported instead of silently repaired.
    /// </summary>
    bool IsProtocolHandlerTakenOver { get; }

    void RegisterProtocolHandler();

    void RestoreProtocolHandler();

    void OpenSystemSettings();
}

/// <summary>
/// Writes and verifies the current user's controller-launch preference, and registers,
/// reports and restores the per-user no-op <see cref="GameBarProtocol.Name"/> handler.
/// Registration records whatever command the protocol had before - including "nothing" -
/// so restore can hand it back. Ownership is the marker AND the live command still being
/// one of this app's no-op handlers: a foreign command under a leftover marker is a
/// takeover that restore refuses to touch, because acting on the marker alone would
/// delete or overwrite the other program's registration. The recorded backup is a
/// per-user snapshot only - it never captured a machine-wide or third-party state, and
/// restore does not pretend it did.
/// </summary>
public sealed class GameBarPopupGuard : IGameBarPopupGuard
{
    private const string PreferenceKeyPath = @"Software\Microsoft\GameBar";
    private const string PreferenceValueName = "UseNexusForGameBarEnabled";

    private const string ProtocolKeyPath = @"Software\Classes\" + GameBarProtocol.Name;
    private const string ShellOpenSubKeyPath = @"shell\open";
    private const string CommandSubKeyPath = ShellOpenSubKeyPath + @"\command";
    private const string CommandKeyPath = ProtocolKeyPath + "\\" + CommandSubKeyPath;
    private const string OwnerValueName = "DeziroJoyOwner";
    private const string PreviousCommandValueName = "DeziroJoyPreviousCommand";
    private const string UrlProtocolValueName = "URL Protocol";

    private readonly Func<bool> _isSupported;
    private readonly IGameBarRegistry _registry;
    private readonly Func<string?> _resolveExecutable;

    public GameBarPopupGuard()
        : this(
            static () => OperatingSystem.IsWindows(),
            new WindowsGameBarRegistry(),
            GameBarProtocol.ResolveExecutable)
    {
    }

    internal GameBarPopupGuard(
        Func<bool> isSupported,
        IGameBarRegistry registry,
        Func<string?>? resolveExecutable = null)
    {
        _isSupported = isSupported;
        _registry = registry;
        _resolveExecutable = resolveExecutable ?? GameBarProtocol.ResolveExecutable;
    }

    public bool IsSupported => _isSupported();

    public bool IsControllerLaunchDisabled
        => IsSupported && _registry.ReadDWord(PreferenceKeyPath, PreferenceValueName) == 0;

    public bool OwnsProtocolHandler
        => IsSupported && HasOwnerMarker && CurrentCommandIsOurs();

    public bool IsProtocolHandlerTakenOver
        => IsSupported && HasOwnerMarker && !CurrentCommandIsOurs();

    public bool IsProtocolHandlerRegistered
    {
        get
        {
            if (!OwnsProtocolHandler)
            {
                return false;
            }

            var expected = ExpectedCommand();
            return expected is not null
                && string.Equals(
                    _registry.ReadString(CommandKeyPath, null),
                    expected,
                    StringComparison.Ordinal);
        }
    }

    private bool HasOwnerMarker
        => _registry.ReadDWord(ProtocolKeyPath, OwnerValueName) == 1;

    /// <summary>
    /// Whether the live command is one of this app's no-op handlers, whatever exe path it
    /// names - the version-independent half of ownership, so a moved app stays ours and a
    /// foreign handler never does.
    /// </summary>
    private bool CurrentCommandIsOurs()
        => _registry.ReadString(CommandKeyPath, null) is { } command
            && command.Contains(GameBarProtocol.NoopArgument, StringComparison.OrdinalIgnoreCase);

    public void DisableControllerLaunch()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Game Bar controller launch is a Windows setting.");
        }

        _registry.WriteDWord(PreferenceKeyPath, PreferenceValueName, 0);
        if (_registry.ReadDWord(PreferenceKeyPath, PreferenceValueName) != 0)
        {
            throw new InvalidOperationException("注册表写入后仍未读到关闭状态，请改用 Windows 系统设置。");
        }
    }

    public void RegisterProtocolHandler()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "The ms-gamebar protocol handler is a per-user Windows registration.");
        }

        var command = GameBarProtocol.BuildCommand(RequireExecutable());
        var current = _registry.ReadString(CommandKeyPath, null);
        var marked = HasOwnerMarker;

        // Remember what the protocol had before this app takes it over, so restore can hand
        // it back. Three cases: nothing there means no foreign default to preserve; our own
        // handler without the marker is not a foreign default either; a foreign command is
        // recorded - but only when no backup exists yet, because the earliest recorded
        // default is the one restore returns to and is never overwritten. A foreign command
        // under our own marker is a takeover: recording it (when nothing is recorded) keeps
        // that case reversible too.
        if (current is null)
        {
            _registry.DeleteValue(ProtocolKeyPath, PreviousCommandValueName);
        }
        else if (current.Contains(GameBarProtocol.NoopArgument, StringComparison.OrdinalIgnoreCase))
        {
            if (!marked)
            {
                _registry.DeleteValue(ProtocolKeyPath, PreviousCommandValueName);
            }
        }
        else if (_registry.ReadString(ProtocolKeyPath, PreviousCommandValueName) is null)
        {
            _registry.WriteString(ProtocolKeyPath, PreviousCommandValueName, current);
        }

        _registry.WriteString(CommandKeyPath, null, command);
        _registry.WriteString(ProtocolKeyPath, UrlProtocolValueName, string.Empty);
        _registry.WriteDWord(ProtocolKeyPath, OwnerValueName, 1);

        if (!IsProtocolHandlerRegistered)
        {
            throw new InvalidOperationException(
                "ms-gamebar 处理程序写入后未能读回，请重试；也可用系统设置手动关闭。");
        }
    }

    public void RestoreProtocolHandler()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "The ms-gamebar protocol handler is a per-user Windows registration.");
        }

        // Nothing of this app's is here at all - a registration it never made, or one whose
        // marker was already removed. Silent, because there is genuinely nothing to report.
        if (!HasOwnerMarker)
        {
            return;
        }

        // The marker says this app wrote here once, but the command must still be one of
        // this app's no-op handlers. A foreign command under the leftover marker means
        // another program took the protocol over, and restoring would delete or overwrite
        // THEIR handler - so nothing is touched: not the command, not the recorded backup.
        // The caller reports the state instead.
        if (!CurrentCommandIsOurs())
        {
            throw new InvalidOperationException(
                "ms-gamebar 处理程序已被其他程序接管（当前命令已不是本应用的），未做任何改动；"
                + "请检查系统设置，或安装/修复 Xbox Game Bar。");
        }

        var previous = _registry.ReadString(ProtocolKeyPath, PreviousCommandValueName);
        if (previous is null)
        {
            // The protocol had no handler before this app registered: remove exactly what
            // was added, and the key itself once nothing of anyone's is left in it.
            _registry.DeleteValue(ProtocolKeyPath, OwnerValueName);
            _registry.DeleteValue(ProtocolKeyPath, UrlProtocolValueName);
            _registry.DeleteSubKeyTree(ProtocolKeyPath, ShellOpenSubKeyPath);
            if (_registry.SubKeyExists(ProtocolKeyPath, "shell")
                && _registry.IsEmptyKey(ProtocolKeyPath + "\\shell"))
            {
                _registry.DeleteSubKeyTree(ProtocolKeyPath, "shell");
            }

            if (_registry.IsEmptyKey(ProtocolKeyPath))
            {
                _registry.DeleteKey(ProtocolKeyPath);
            }
        }
        else
        {
            _registry.WriteString(CommandKeyPath, null, previous);
            _registry.DeleteValue(ProtocolKeyPath, OwnerValueName);
            _registry.DeleteValue(ProtocolKeyPath, PreviousCommandValueName);
        }

        if (_registry.ReadDWord(ProtocolKeyPath, OwnerValueName) == 1)
        {
            throw new InvalidOperationException("未能移除本应用的 ms-gamebar 处理程序注册，请重试。");
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

    private string? ExpectedCommand()
        => _resolveExecutable() is { } executable
            ? GameBarProtocol.BuildCommand(executable)
            : null;

    private string RequireExecutable()
        => _resolveExecutable()
            ?? throw new InvalidOperationException(
                "请直接运行构建产物 DeziroJoy.exe，不要通过 dotnet run，否则协议处理程序无法启动应用。");
}
