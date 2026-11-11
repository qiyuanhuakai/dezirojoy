namespace DeziroJoy.Platform.Linux;

/// <summary>
/// The device-access facts every Linux surface quotes: the group command, the opt-in udev
/// rules and the linger note. They live in one place so the evdev scan diagnostic, the uinput
/// error, the settings card, the daemon log and the docs all print the same words - a user
/// who copies the rule out of one place must not get a different rule out of another.
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
    /// The rules themselves: the input group may read every event node, and /dev/uinput gets a
    /// static node the same group may write - the kernel module alone does not create it.
    /// </summary>
    public const string UdevRulesText =
        "KERNEL==\"event*\",GROUP=\"input\",MODE=\"0660\"；" +
        "KERNEL==\"uinput\",GROUP=\"input\",MODE=\"0660\",OPTIONS+=\"static_node=uinput\"";

    /// <summary>Keeps the user's systemd instance - and so the resident daemon - alive after logout.</summary>
    public const string LingerCommand = "loginctl enable-linger $USER";

    /// <summary>
    /// The permission fix as one quotable sentence: read access for the pads, write access for
    /// the virtual device, both through the same group or the same rules file.
    /// </summary>
    public const string DeviceAccessGuidance =
        "读取手柄与写入 /dev/uinput 都需要 input 组权限：" + InputGroupCommand + " 后重新登录，" +
        "或添加 udev 规则 " + UdevRulesPath + "：" + UdevRulesText;
}
