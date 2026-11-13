using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Services;

/// <summary>
/// Outcome of a scheme file transfer. <see cref="Message"/> is a ready-to-show Chinese status
/// line. The scheme bundle is the single import/export design, so a transfer reports what
/// happened rather than handing back a profile: an import's effect is applied to the device
/// through the preview and undo flow, not returned to the caller.
/// </summary>
public sealed record ProfileTransferResult(bool Succeeded, string Message)
{
    public static ProfileTransferResult Failed(string message)
        => new(false, message);

    public static ProfileTransferResult SchemeExported(string path)
        => new(true, $"方案已导出：{System.IO.Path.GetFileName(path)}");

    public static ProfileTransferResult SchemeImported(string name)
        => new(true, $"已导入方案：{name}");

    public static ProfileTransferResult SchemeImportUndone()
        => new(true, "已撤销方案导入，设备已恢复导入前的预设。");
}

/// <summary>
/// Export of a whole scheme as one bundle file - the single import/export design. The only
/// codec is <see cref="SchemeBundleCodec"/>, so an exported file is byte-for-byte what the
/// import reads back, and a write that fails comes back as a failed result carrying the
/// reason, so the caller can report it without unwinding anything the user is doing.
/// </summary>
public static class ProfileTransferService
{
    /// <summary>
    /// File extension used for exported schemes. A bundle is JSON too - its profiles are the
    /// same bytes a profile file holds - so it shares the extension and differs by content.
    /// </summary>
    public const string SchemeExtension = ".json";

    /// <summary>Human-readable file type name the picker shows for schemes.</summary>
    public const string SchemeTypeName = "DeziroJoy 方案包";

    /// <summary>Glob patterns the picker accepts for schemes.</summary>
    public static readonly string[] SchemePatterns = ["*.json"];

    /// <summary>
    /// Writes a whole scheme - every preset, the device-global special keys, the temporary
    /// layers, action names, the writing version and usage notes - as one bundle file. The
    /// extension is normalized the same way a save dialog expects, and a write that fails
    /// comes back as a failed result carrying the reason.
    /// </summary>
    public static ProfileTransferResult ExportScheme(SchemeBundle bundle, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return ProfileTransferResult.Failed("导出失败：未选择保存位置。");
        }

        try
        {
            var path = destinationPath.EndsWith(SchemeExtension, StringComparison.OrdinalIgnoreCase)
                ? destinationPath
                : destinationPath + SchemeExtension;

            File.WriteAllText(path, SchemeBundleCodec.Serialize(bundle));

            return ProfileTransferResult.SchemeExported(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ProfileTransferResult.Failed($"导出失败：{ex.Message}");
        }
    }
}
