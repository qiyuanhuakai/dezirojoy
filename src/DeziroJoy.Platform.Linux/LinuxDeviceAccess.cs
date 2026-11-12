namespace DeziroJoy.Platform.Linux;

/// <summary>
/// The device-access facts every Linux surface quotes: the group command, the opt-in udev
/// rules, the linger note, and the one-click fix's helper path and notice. They live in one
/// place so the evdev scan diagnostic, the uinput error, the settings card, the fix helper
/// invocation, the daemon log and the docs all print the same words - a user who copies the
/// rule out of one place must not get a different rule out of another.
/// </summary>
public static class LinuxDeviceAccess
{
    /// <summary>Grants the current user read access to the evdev nodes; needs a re-login.</summary>
    public const string InputGroupCommand = "sudo usermod -aG input $USER";

    /// <summary>
    /// Where the opt-in udev rules belong. The installers deliberately never write here:
    /// registering udev rules is a machine-wide change the user opts into from inside the app
    /// (docs/QA-CHECKLIST.md 安装包一节).
    /// </summary>
    public const string UdevRulesPath = "/etc/udev/rules.d/99-dezirojoy.rules";

    /// <summary>
    /// The rules themselves, one rule per line: the input group may read every event node,
    /// and /dev/uinput gets a static node the same group may write - the kernel module alone
    /// does not create it. Newline-joined on purpose: a user copies this verbatim into the
    /// rules file, and udev rules are line-based, so a single joined line would apply neither.
    /// </summary>
    public const string UdevRulesText =
        "KERNEL==\"event*\",GROUP=\"input\",MODE=\"0660\"" + "\n" +
        "KERNEL==\"uinput\",GROUP=\"input\",MODE=\"0660\",OPTIONS+=\"static_node=uinput\"";

    /// <summary>Keeps the user's systemd instance - and so the resident daemon - alive after logout.</summary>
    public const string LingerCommand = "loginctl enable-linger $USER";

    /// <summary>
    /// The file name of the packaged one-click fix helper, shipped beside the application binary
    /// by every Linux format (.deb, .rpm and the AppImage's mounted AppDir).
    /// </summary>
    private const string FixHelperFileName = "fix-device-access.sh";

    /// <summary>
    /// The absolute location the .deb and .rpm install the helper to. Kept as the fallback for a
    /// copy whose <see cref="AppContext.BaseDirectory"/> is not its install directory; the helper
    /// beside the running binary is always preferred when it exists.
    /// </summary>
    private const string DebFixHelperPath = "/opt/dezirojoy/fix-device-access.sh";

    /// <summary>
    /// Where the packaged one-click fix helper lives for THIS install form. The installers ship it
    /// beside the application binary - <c>/opt/dezirojoy</c> for the .deb and .rpm, the mounted
    /// AppDir's <c>opt/dezirojoy</c> for an AppImage - so it is resolved relative to the running
    /// binary first, which is the payload directory on every packaged format; the absolute deb
    /// path is the fallback. The installers ship the helper but never run it: the app invokes it
    /// through pkexec only when the user asks for the fix, so the opt-in principle (安装器不注册规则)
    /// survives the one-click path.
    /// </summary>
    public static string FixHelperPath
    {
        get
        {
            var beside = Path.Combine(AppContext.BaseDirectory, FixHelperFileName);
            return File.Exists(beside) ? beside : DebFixHelperPath;
        }
    }

    /// <summary>
    /// What the one-click fix changes on the machine, stated before the user takes it: the
    /// udev rules file, the input group, and the re-login the group change needs.
    /// </summary>
    public const string FixNotice =
        "一键修复将写入 " + UdevRulesPath + "（udev 规则）并把当前用户加入 input 组，需要重新登录后生效。";

    /// <summary>
    /// The permission fix as one quotable sentence: read access for the pads, write access for
    /// the virtual device, both through the same group or the same rules file.
    /// </summary>
    public const string DeviceAccessGuidance =
        "读取手柄与写入 /dev/uinput 都需要 input 组权限：" + InputGroupCommand + " 后重新登录，" +
        "或添加 udev 规则 " + UdevRulesPath + "：" + UdevRulesText;
}
