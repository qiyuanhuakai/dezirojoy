using System.Text.Json;

namespace DeziroJoy.App.Services;

/// <summary>One legacy→SDL device pair the configuration migration has already processed.</summary>
public readonly record struct LegacyMigrationPair(string LegacyDeviceId, string SdlDeviceId);

/// <summary>
/// The record of legacy→SDL configuration pairs the migration has already processed. A pair
/// is processed exactly once: the first run copies the old backend's presets onto the SDL
/// device - or deliberately declines to - and every later run, whether a reconnect, a
/// restart or a second window, leaves that device alone.
/// <para>
/// The record is what keeps the migration from undoing the user's own edits after it. A slot
/// the user deleted would otherwise be refilled from the old configuration on the next
/// connect, and a slot the user deliberately reset to the library defaults would otherwise
/// be quietly replaced by the old configuration again - both are the "my presets came back"
/// report this exists to prevent.
/// </para>
/// </summary>
public interface ILegacyMigrationMarkers
{
    /// <summary>
    /// The pairs already processed, or null when the record could not be read. Null is not
    /// "nothing processed": a record that cannot be read cannot prove that any pair is new,
    /// so the migration declines to touch anything rather than risk overwriting the user's
    /// work. A missing file is the empty record - every install's first run.
    /// </summary>
    IReadOnlyList<LegacyMigrationPair>? Load();

    void Mark(LegacyMigrationPair pair);
}

/// <summary>
/// One JSON file in the configuration root, beside the profiles directory rather than
/// inside it: <see cref="FileProfileStore"/> enumerates that directory, and a migration
/// record in it would be read back as a device's profile. Written through the profiles'
/// own atomic writer, so a daemon reading the record while a save lands sees either the old
/// body or the new one.
/// </summary>
public sealed class LegacyMigrationMarkerFile : ILegacyMigrationMarkers
{
    public const string FileName = "legacy-migration.json";

    private readonly string _path;

    private readonly Lock _gate = new();

    /// <summary>The pairs read from disk, or null before the first read.</summary>
    private List<LegacyMigrationPair>? _cache;

    /// <summary>True once a read has failed; the record stays unusable for this process.</summary>
    private bool _unreadable;

    public LegacyMigrationMarkerFile(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeziroJoy",
            FileName);
    }

    public string FilePath => _path;

    public IReadOnlyList<LegacyMigrationPair>? Load()
    {
        lock (_gate)
        {
            if (_unreadable)
            {
                return null;
            }

            if (_cache is not null)
            {
                return _cache;
            }

            var read = Read();
            if (read is null)
            {
                // Damage is remembered for this process rather than re-read on every call:
                // the answer must not flip mid-run while another writer repairs the file.
                _unreadable = true;
                return null;
            }

            _cache = read;
            return _cache;
        }
    }

    public void Mark(LegacyMigrationPair pair)
    {
        lock (_gate)
        {
            var processed = Load();

            if (processed is null)
            {
                // A record that cannot be read is not appended to: a half-merged write would
                // drop the pairs it failed to parse, and the migration would repeat them.
                return;
            }

            if (processed.Contains(pair))
            {
                return;
            }

            var updated = processed.ToList();
            updated.Add(pair);
            Write(updated);
            _cache = updated;
        }
    }

    /// <summary>
    /// The file's body as pairs, an empty record when there is no file, or null when the
    /// body cannot be read or parsed. A corrupt record is null rather than empty: the caller
    /// must not treat damage as proof that nothing was ever processed.
    /// </summary>
    private List<LegacyMigrationPair>? Read()
    {
        string body;
        try
        {
            body = File.Exists(_path) ? File.ReadAllText(_path) : "{}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<Record>(body);
            if (record is null)
            {
                // A JSON null is a damaged body, not an empty record: deserializing it
                // succeeds with null, and treating that as "nothing processed" would let a
                // repeated migration overwrite work the record was meant to protect.
                return null;
            }

            return record.Processed?.ToList() ?? [];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Write(IReadOnlyList<LegacyMigrationPair> processed)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        FileProfileStore.WriteAllTextAtomic(
            _path,
            JsonSerializer.Serialize(new Record { Processed = processed }));
    }

    private sealed class Record
    {
        public IReadOnlyList<LegacyMigrationPair>? Processed { get; init; }
    }
}

/// <summary>Test/embedder default that never touches the signed-in user's real configuration.</summary>
public sealed class InMemoryLegacyMigrationMarkers : ILegacyMigrationMarkers
{
    private readonly List<LegacyMigrationPair> _processed = [];

    /// <summary>When set, every read answers as an unreadable record, as damage would.</summary>
    public bool Unreadable { get; set; }

    public IReadOnlyList<LegacyMigrationPair> Marked => _processed;

    public IReadOnlyList<LegacyMigrationPair>? Load() => Unreadable ? null : _processed;

    public void Mark(LegacyMigrationPair pair)
    {
        if (!_processed.Contains(pair))
        {
            _processed.Add(pair);
        }
    }
}
