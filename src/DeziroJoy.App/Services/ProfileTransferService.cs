using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Services;

/// <summary>
/// Outcome of a profile file transfer. <see cref="Profile"/> is set only by a successful import;
/// <see cref="Message"/> is a ready-to-show Chinese status line.
/// </summary>
public sealed record ProfileTransferResult(bool Succeeded, GamepadProfile? Profile, string Message)
{
    public static ProfileTransferResult Exported(string path)
        => new(true, null, $"配置已导出：{System.IO.Path.GetFileName(path)}");

    public static ProfileTransferResult Imported(GamepadProfile profile)
        => new(true, profile, $"已导入配置：{profile.Name}");

    public static ProfileTransferResult Failed(string message)
        => new(false, null, message);

    public static ProfileTransferResult SchemeExported(string path)
        => new(true, null, $"方案已导出：{System.IO.Path.GetFileName(path)}");

    public static ProfileTransferResult SchemeImported(string name)
        => new(true, null, $"已导入方案：{name}");

    public static ProfileTransferResult SchemeImportUndone()
        => new(true, null, "已撤销方案导入，设备已恢复导入前的预设。");
}

/// <summary>
/// Import/export of profile files: a single profile as a JSON file, or a whole scheme as a
/// bundle. The only codecs are <see cref="ProfileSerializer"/> and <see cref="SchemeBundleCodec"/>,
/// so an exported file is byte-for-byte what the per-device store writes and can be dropped
/// straight into the profiles directory, and an exported bundle carries its profiles in exactly
/// that shape. Reading never throws for bad content: a malformed file comes back as a failed
/// result carrying the reason, so the caller can report it without unwinding the profile the
/// user is currently editing.
/// </summary>
public static class ProfileTransferService
{
    /// <summary>File extension used for exported profiles and offered by the picker.</summary>
    public const string Extension = ".json";

    /// <summary>Human-readable file type name shown in the picker.</summary>
    public const string TypeName = "DeziroJoy 配置文件";

    /// <summary>Glob patterns the picker accepts.</summary>
    public static readonly string[] Patterns = ["*.json"];

    /// <summary>
    /// File extension used for exported schemes. A bundle is JSON too - its profiles are the
    /// same bytes a profile file holds - so it shares the extension and differs by content.
    /// </summary>
    public const string SchemeExtension = ".json";

    /// <summary>Human-readable file type name the picker shows for schemes.</summary>
    public const string SchemeTypeName = "DeziroJoy 方案包";

    /// <summary>Glob patterns the picker accepts for schemes.</summary>
    public static readonly string[] SchemePatterns = ["*.json"];

    public static ProfileTransferResult Export(GamepadProfile profile, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return ProfileTransferResult.Failed("导出失败：未选择保存位置。");
        }

        try
        {
            var path = destinationPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
                ? destinationPath
                : destinationPath + Extension;

            // Same normalization the per-device store applies, so an exported file is always
            // current-schema and loads anywhere without a migration step.
            var json = ProfileSerializer.Serialize(profile with { SchemaVersion = GamepadProfile.CurrentSchemaVersion });

            File.WriteAllText(path, json);

            return ProfileTransferResult.Exported(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ProfileTransferResult.Failed($"导出失败：{ex.Message}");
        }
    }

    public static ProfileTransferResult Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return ProfileTransferResult.Failed("导入失败：未选择文件。");
        }

        if (!File.Exists(sourcePath))
        {
            return ProfileTransferResult.Failed("导入失败：文件不存在。");
        }

        string json;
        try
        {
            json = File.ReadAllText(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ProfileTransferResult.Failed($"导入失败：无法读取文件（{ex.Message}）");
        }

        GamepadProfile profile;
        try
        {
            profile = ProfileSerializer.Deserialize(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ProfileTransferResult.Failed($"导入失败：不是有效的 DeziroJoy 配置（{ex.Message}）");
        }

        if (profile.SchemaVersion > GamepadProfile.CurrentSchemaVersion)
        {
            return ProfileTransferResult.Failed(
                $"导入失败：配置版本过新（v{profile.SchemaVersion}），本应用最高支持 v{GamepadProfile.CurrentSchemaVersion}");
        }

        return ProfileTransferResult.Imported(profile);
    }

    /// <summary>
    /// Writes a whole scheme - every preset, the device-global special keys, the temporary
    /// layers, action names, the writing version and usage notes - as one bundle file. The
    /// extension is normalized the same way the single-profile export normalizes it, and a
    /// write that fails comes back as a failed result carrying the reason.
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
