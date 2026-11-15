using System.Text.Json;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Services;

/// <summary>
/// The complete pre-import state needed by the one-level scheme undo. Unlike the old
/// window-owned snapshot, this record is serializable: the render process may exit and a
/// later one can still restore every slot, the app-wide active slot and, when the import
/// also applied them, the previous foreground rules.
/// </summary>
public sealed record SchemeImportRecovery
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string DeviceId { get; init; }

    public required string SchemeName { get; init; }

    public required IReadOnlyDictionary<int, GamepadProfile?> Slots { get; init; }

    public int ActiveSlot { get; init; } = PresetStore.MinSlot;

    /// <summary>Null when that import left the global foreground rule table untouched.</summary>
    public ForegroundSettings? ForegroundRules { get; init; }

    public DateTimeOffset ImportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Persistence seam for the one scheme-import recovery point.</summary>
public interface ISchemeImportRecoveryStore
{
    /// <summary>
    /// The last readable recovery point, or null when none exists. Implementations report a
    /// damaged or unreadable snapshot through <see cref="LastError"/> rather than stopping
    /// the GUI from opening.
    /// </summary>
    SchemeImportRecovery? Load();

    string? LastError { get; }

    /// <summary>Replaces the recovery point. A failed save must throw and abort the import.</summary>
    void Save(SchemeImportRecovery recovery);

    /// <summary>Removes the recovery point only after a successful undo.</summary>
    void Clear();
}

/// <summary>
/// The production recovery store. Writes use a same-directory temporary file and rename so
/// a GUI/process exit cannot leave a partially written JSON body at the recovery path.
/// </summary>
public sealed class FileSchemeImportRecoveryStore : ISchemeImportRecoveryStore
{
    public const string FileName = "scheme-import-recovery.json";

    private readonly string _path;

    public FileSchemeImportRecoveryStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeziroJoy",
            FileName);
    }

    public string FilePath => _path;

    public string? LastError { get; private set; }

    public SchemeImportRecovery? Load()
    {
        LastError = null;

        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var recovery = JsonSerializer.Deserialize<SchemeImportRecovery>(
                File.ReadAllText(_path), ProfileSerializer.Options);

            if (recovery is null)
            {
                throw new JsonException("恢复点内容为空。");
            }

            if (recovery.SchemaVersion > SchemeImportRecovery.CurrentSchemaVersion)
            {
                throw new JsonException($"恢复点版本过新（v{recovery.SchemaVersion}）。");
            }

            if (string.IsNullOrWhiteSpace(recovery.DeviceId)
                || recovery.ActiveSlot < PresetStore.MinSlot
                || recovery.Slots is null
                || recovery.Slots.Any(pair => pair.Key < PresetStore.MinSlot))
            {
                throw new JsonException("恢复点的设备或槽位信息无效。");
            }

            return recovery;
        }
        catch (Exception exception) when (exception
            is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastError = $"方案导入恢复点无法读取：{exception.Message}";
            return null;
        }
    }

    public void Save(SchemeImportRecovery recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(recovery, ProfileSerializer.Options));
            File.Move(temporary, _path, overwrite: true);
            LastError = null;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        LastError = null;
    }
}

/// <summary>
/// Process-local default for view-model tests and embedders. Production explicitly injects
/// <see cref="FileSchemeImportRecoveryStore"/> at the composition root.
/// </summary>
public sealed class InMemorySchemeImportRecoveryStore : ISchemeImportRecoveryStore
{
    private SchemeImportRecovery? _recovery;

    public string? LastError => null;

    public SchemeImportRecovery? Load() => _recovery;

    public void Save(SchemeImportRecovery recovery) => _recovery = recovery;

    public void Clear() => _recovery = null;
}
