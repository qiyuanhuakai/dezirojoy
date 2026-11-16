using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// One persisted SDL gamepad mapping, keyed by GUID. The mapping string is the exact text
/// <c>SDL_AddGamepadMapping</c> accepts, so the file is hand-editable and matches what the
/// SDL controller database uses; the GUID and name are kept beside it so the file is readable
/// and a repeat capture of the same device updates its entry instead of adding a second one.
/// </summary>
public sealed record Sdl3MappingEntry
{
    /// <summary>32-character hex GUID this mapping applies to.</summary>
    public required string GuidString { get; init; }

    /// <summary>Device name as the user knows it; becomes the mapping's name field.</summary>
    public required string DeviceName { get; init; }

    /// <summary>The full SDL mapping string, starting with <see cref="GuidString"/>.</summary>
    public required string Mapping { get; init; }

    /// <summary>
    /// When this entry was last written, as a round-trip ISO-8601 UTC string. Evidence for
    /// the user reading the file back; it never influences which mapping SDL applies.
    /// </summary>
    public required string UpdatedUtc { get; init; }
}

/// <summary>
/// What reading the mapping file produced: the entries to hand to SDL, plus the reason the
/// file could not be read whole when it could not. A missing file is an empty snapshot with no
/// problem - that is the state every install starts from - while an unreadable or unparsable
/// one keeps whatever SDL already knows and reports why instead of failing the scan.
/// </summary>
/// <param name="Problems">
/// Entries that read whole but do not describe a mapping SDL will honour. They are still
/// returned in <paramref name="Mappings"/> - deleting a user's file over a typo would cost them
/// more than applying the parts that do work - and reported here so the daemon can say which
/// device is only partly mapped instead of the user finding out at the button.
/// </param>
public sealed record Sdl3MappingSnapshot(
    IReadOnlyList<Sdl3MappingEntry> Mappings,
    string? Problem,
    IReadOnlyList<string>? Problems = null)
{
    /// <summary>Entries that read whole but do not describe a mapping SDL will honour.</summary>
    public IReadOnlyList<string> Problems { get; init; } = Problems ?? [];
}

/// <summary>
/// Where persisted SDL mappings come from. The provider takes one of these rather than a
/// path so the production source - the config root's mapping file - is injected exactly where
/// the daemon is composed, and a test can hand the provider entries without touching the
/// signed-in user's configuration.
/// </summary>
public interface ISdl3MappingSource
{
    Sdl3MappingSnapshot Load();
}

/// <summary>
/// The production source: the mapping file in the configuration root, beside
/// <c>input-backend.json</c> and outside the profiles directory.
/// </summary>
public sealed class Sdl3MappingFileSource : ISdl3MappingSource
{
    public Sdl3MappingFileSource(string? path = null)
    {
        Path = path ?? Sdl3MappingFile.DefaultPath;
    }

    public string Path { get; }

    public Sdl3MappingSnapshot Load() => Sdl3MappingFile.Load(Path);
}

/// <summary>
/// Serialization, storage and validation of <c>sdl-mappings.json</c>. Everything here is pure
/// text handling over a path - no SDL, no device - so the file format, the merge rules and the
/// diagnostics for a hand-broken file are covered by deterministic tests. Writes are atomic:
/// a daemon reading the file while the capture flow saves must see the previous body or the
/// new one, never half of one.
/// </summary>
public static class Sdl3MappingFile
{
    public const string FileName = "sdl-mappings.json";

    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        // Device names and mapping text are Chinese-friendly ASCII; the relaxed encoder keeps
        // them readable when the file is opened by hand.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The configuration-root path: <c>%APPDATA%\DeziroJoy\sdl-mappings.json</c> on Windows,
    /// <c>~/.config/DeziroJoy/sdl-mappings.json</c> on Linux - the same directory
    /// <c>input-backend.json</c> lives in.
    /// </summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeziroJoy",
        FileName);

    /// <summary>
    /// Reads the file. A missing file is an empty snapshot with no problem; a body that is not
    /// JSON, or whose entries are unusable, is reported through <see cref="Sdl3MappingSnapshot.Problem"/>
    /// and yields no entries - the daemon then runs on SDL's built-in database alone and says
    /// so, rather than guessing what the broken file meant.
    /// </summary>
    public static Sdl3MappingSnapshot Load(string path)
    {
        string body;
        try
        {
            if (!File.Exists(path))
            {
                return new Sdl3MappingSnapshot([], null);
            }

            body = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new Sdl3MappingSnapshot([], $"SDL 映射文件无法读取：{exception.Message}");
        }

        StoredFile? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredFile>(body, DeserializeOptions);
        }
        catch (JsonException exception)
        {
            return new Sdl3MappingSnapshot([], $"SDL 映射文件无法解析，已只使用 SDL 内置映射库：{exception.Message}");
        }

        if (stored?.Mappings is null)
        {
            return new Sdl3MappingSnapshot([], null);
        }

        var entries = new List<Sdl3MappingEntry>();
        foreach (var storedEntry in stored.Mappings)
        {
            if (storedEntry is null)
            {
                continue;
            }

            var guid = (storedEntry.GuidString ?? string.Empty).Trim();
            var name = (storedEntry.DeviceName ?? string.Empty).Trim();
            var mapping = (storedEntry.Mapping ?? string.Empty).Trim();
            var updated = (storedEntry.UpdatedUtc ?? string.Empty).Trim();

            if (guid.Length == 0 || mapping.Length == 0)
            {
                continue;
            }

            var entry = new Sdl3MappingEntry
            {
                GuidString = guid,
                DeviceName = name,
                Mapping = mapping,
                UpdatedUtc = updated.Length == 0 ? "1970-01-01T00:00:00.0000000Z" : updated,
            };

            entries.Add(entry);
        }

        var problems = new List<string>();
        foreach (var entry in Deduplicate(entries))
        {
            problems.AddRange(Sdl3MappingFile.Validate(entry).Select(problem => $"{entry.DeviceName}：{problem}"));
        }

        return new Sdl3MappingSnapshot(Deduplicate(entries), null, problems);
    }

    /// <summary>
    /// Writes one entry, merging it into whatever the file holds. A GUID that is already
    /// present is replaced - re-capturing a device updates its mapping instead of stacking a
    /// second, ambiguous entry - and every other entry is preserved untouched. The file is
    /// written sorted by GUID so the same set of entries always produces the same bytes.
    /// </summary>
    public static Sdl3MappingSaveResult Save(string path, Sdl3MappingEntry entry)
    {
        var problems = Validate(entry);
        if (problems.Count > 0)
        {
            return new Sdl3MappingSaveResult(false, problems, entry);
        }

        var existing = Load(path);
        if (existing.Problem is not null)
        {
            // Refuse to overwrite a file we could not read whole: the user would lose mappings
            // this build cannot see. The reason is reported so the file can be repaired first.
            return new Sdl3MappingSaveResult(false, [existing.Problem], entry);
        }

        var merged = Deduplicate([.. existing.Mappings, entry]);

        var stored = new StoredFile
        {
            Version = FormatVersion,
            Mappings = merged
                .Select(item => new StoredEntry
                {
                    GuidString = item.GuidString,
                    DeviceName = item.DeviceName,
                    Mapping = item.Mapping,
                    UpdatedUtc = item.UpdatedUtc,
                })
                .ToList<StoredEntry?>(),
        };

        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            WriteAllTextAtomic(path, JsonSerializer.Serialize(stored, SerializeOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new Sdl3MappingSaveResult(false, [$"SDL 映射文件无法写入：{exception.Message}"], entry);
        }

        return new Sdl3MappingSaveResult(true, [], entry);
    }

    /// <summary>
    /// Removes duplicate GUIDs, keeping the last occurrence of each. A re-captured mapping is
    /// appended after the entries read from disk, so it wins; a hand-edited file with repeats
    /// resolves the same way every time it is read.
    /// </summary>
    public static IReadOnlyList<Sdl3MappingEntry> Deduplicate(IReadOnlyList<Sdl3MappingEntry> entries)
    {
        var byGuid = new Dictionary<string, Sdl3MappingEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var entry in entries)
        {
            if (!byGuid.ContainsKey(entry.GuidString))
            {
                order.Add(entry.GuidString);
            }

            byGuid[entry.GuidString] = entry;
        }

        return order
            .Select(guid => byGuid[guid])
            .OrderBy(entry => entry.GuidString, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Everything wrong with one entry, in Chinese, so the capture flow can refuse to save a
    /// mapping SDL would reject and say why. The rules mirror what SDL's own mapping parser
    /// accepts: a 32-hex-digit GUID that matches the mapping's first field, a non-empty name,
    /// at least one binding, and binding elements whose values are a button, an axis or a hat.
    /// </summary>
    public static IReadOnlyList<string> Validate(Sdl3MappingEntry entry)
    {
        var problems = new List<string>();

        if (!IsGuid(entry.GuidString))
        {
            problems.Add($"GUID「{entry.GuidString}」不是 32 位十六进制字符串。");
        }

        if (string.IsNullOrWhiteSpace(entry.Mapping))
        {
            problems.Add("映射字符串为空。");
            return problems;
        }

        var fields = entry.Mapping.Split(',');
        if (fields.Length < 3)
        {
            problems.Add("映射字符串至少需要 GUID、名称和一个按键/轴绑定。");
            return problems;
        }

        if (!string.Equals(fields[0].Trim(), entry.GuidString, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("映射字符串的第一个字段必须与 GUID 相同。");
        }

        var name = fields[1].Trim();
        if (name.Length == 0)
        {
            problems.Add("映射名称为空。");
        }

        for (var i = 2; i < fields.Length; i++)
        {
            var element = fields[i].Trim();
            if (element.Length == 0)
            {
                continue;
            }

            var separator = element.IndexOf(':');
            if (separator <= 0 || separator == element.Length - 1)
            {
                problems.Add($"映射元素「{element}」不是「名称:值」的形式。");
                continue;
            }

            var key = element[..separator];
            var value = element[(separator + 1)..];

            if (PositionalFaceKeyPattern.IsMatch(key))
            {
                // The one mistake worth its own message: SDL's parser matches its key table
                // exactly and drops an element it does not recognize *without an error*, so a
                // mapping written this way loads, applies and silently has no face buttons.
                problems.Add(
                    $"映射元素「{element}」用了 SDL 的位置名；SDL 的映射键是 a／b／x／y，"
                    + $"「{key}」会被静默忽略，对应按键不会生效。");
                continue;
            }

            if (!KeyPattern.IsMatch(key))
            {
                problems.Add($"映射元素「{element}」的名称「{key}」无法识别。");
                continue;
            }

            if (ControlKeyPattern.IsMatch(key) && !ControlTokenPattern.IsMatch(value))
            {
                problems.Add(
                    $"映射元素「{element}」的值「{value}」不是 bN、aN、aN~、+aN、-aN 或 hN.mask 形式的控件。");
            }
        }

        return problems;
    }

    /// <summary>Builds a validated entry, or null when the arguments do not describe a mapping.</summary>
    public static Sdl3MappingEntry? TryBuild(string deviceGuid, string deviceName, string mapping)
    {
        var entry = new Sdl3MappingEntry
        {
            GuidString = deviceGuid,
            DeviceName = deviceName,
            Mapping = mapping,
            UpdatedUtc = Stamp(),
        };

        return Validate(entry).Count == 0 ? entry : null;
    }

    /// <summary>The timestamp a new entry carries.</summary>
    public static string Stamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static bool IsGuid(string guid)
        => guid.Length == 32 && guid.All(c => Uri.IsHexDigit(c));

    private static readonly Regex KeyPattern = new(@"^[-+]?[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// Keys SDL reads as a standard gamepad control, whose value must be a control token. The
    /// face keys are a/b/x/y - SDL's own spelling, not the enum's South/East/West/North.
    /// </summary>
    private static readonly Regex ControlKeyPattern = new(
        @"^[-+]?(a|b|x|y|dpup|dpdown|dpleft|dpright|start|back|guide|"
        + @"leftshoulder|rightshoulder|lefttrigger|righttrigger|leftstick|rightstick|"
        + @"leftx|lefty|rightx|righty|misc[1-9]|paddle[1-4]|touchpad)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The positional names SDL does not accept, reported with their own message.</summary>
    private static readonly Regex PositionalFaceKeyPattern = new(
        @"^[-+]?(south|east|west|north)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ControlTokenPattern = new(
        @"^[-+]?(b[0-9]+|[ah][0-9]+(\.[0-9]+)?)~?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The same temp-file-then-replace write the profiles use, so a reader never observes a
    /// truncated file. A failed replace still removes the temporary body.
    /// </summary>
    private static void WriteAllTextAtomic(string path, string contents)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class StoredFile
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("mappings")]
        public List<StoredEntry?>? Mappings { get; init; }
    }

    private sealed class StoredEntry
    {
        [JsonPropertyName("guid")]
        public string? GuidString { get; init; }

        [JsonPropertyName("name")]
        public string? DeviceName { get; init; }

        [JsonPropertyName("mapping")]
        public string? Mapping { get; init; }

        [JsonPropertyName("updatedUtc")]
        public string? UpdatedUtc { get; init; }
    }
}

/// <summary>
/// What one save attempt produced: whether the file was written, every problem that stopped it
/// (empty on success), and the entry that was written or refused.
/// </summary>
public sealed record Sdl3MappingSaveResult(
    bool Saved,
    IReadOnlyList<string> Problems,
    Sdl3MappingEntry Entry)
{
    /// <summary>Every problem that stopped the save, in one line; empty on success.</summary>
    public string Describe() => string.Join("；", Problems);
}

/// <summary>
/// Formats a mapping file for a human, with the GUID alignment the probe prints. Text only -
/// nothing here reads or writes the file.
/// </summary>
public static class Sdl3MappingFileFormat
{
    public static IReadOnlyList<string> Describe(Sdl3MappingSnapshot snapshot)
    {
        var lines = new List<string>
        {
            $"持久 SDL 映射：{snapshot.Mappings.Count} 条"
            + (snapshot.Problem is null ? string.Empty : $"（读取问题：{snapshot.Problem}）"),
        };

        foreach (var entry in snapshot.Mappings)
        {
            var builder = new StringBuilder("    ");
            builder.Append(entry.GuidString);
            builder.Append("  ");
            builder.Append(entry.DeviceName.Length == 0 ? "(未命名)" : entry.DeviceName);
            builder.Append("  更新于 ");
            builder.Append(entry.UpdatedUtc);
            lines.Add(builder.ToString());
            lines.Add($"      {entry.Mapping}");
        }

        return lines;
    }
}
