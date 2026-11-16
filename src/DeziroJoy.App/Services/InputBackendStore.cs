using System.Text.Json;

namespace DeziroJoy.App.Services;

/// <summary>
/// The persisted input-backend selection: one JSON file beside the profiles and the
/// foreground rules, never inside the profiles directory - <see cref="FileProfileStore"/>
/// enumerates that directory, and a settings file in it would be read back as a device's
/// profile.
/// <para>
/// A missing file is <see cref="InputBackend.Platform"/>, the state every existing install
/// starts from: a build that knows nothing about this setting keeps reading through XInput
/// or evdev exactly as it did. A damaged file costs the selection and nothing else - the
/// platform default runs - and the reason is kept for the settings card to show.
/// </para>
/// <para>
/// The daemon reads this at startup (there is no control-channel verb for a backend
/// switch), so a save from the settings card reaches the mapping authority through the
/// safe restart the card asks for rather than through a hot swap of the provider.
/// </para>
/// </summary>
public interface IInputBackendStore
{
    /// <summary>Why the last read fell back to the platform default, or null when it read whole.</summary>
    string? LastError { get; }

    InputBackend Load();

    void Save(InputBackend backend);
}

/// <summary>Test/embedder default that never touches the signed-in user's real AppData.</summary>
public sealed class InMemoryInputBackendStore : IInputBackendStore
{
    private InputBackend _backend = InputBackend.Platform;

    public string? LastError { get; private set; }

    public InputBackend Load() => _backend;

    public void Save(InputBackend backend)
    {
        _backend = backend;
        LastError = null;
    }
}

public sealed class InputBackendStore : IInputBackendStore
{
    public const string FileName = "input-backend.json";

    private readonly string _path;

    public InputBackendStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeziroJoy",
            FileName);
    }

    /// <summary>Why the last read fell back to the platform default, or null when the file read whole.</summary>
    public string? LastError { get; private set; }

    public string FilePath => _path;

    public InputBackend Load()
    {
        var body = ReadBody();

        if (body is null)
        {
            // No file is the platform default every install starts from, and says nothing;
            // an unreadable one is a failure worth quoting.
            LastError = _readFailure;
            return InputBackend.Platform;
        }

        var (backend, problem) = ReadBackend(body);
        LastError = problem;
        return backend;
    }

    public void Save(InputBackend backend)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var body = JsonSerializer.Serialize(new StoredBackend { Backend = InputBackendText.ToName(backend) });

        // The same atomic write the profiles use: a daemon reading the selection while the
        // card saves must see either the old body or the new one, never half of one.
        FileProfileStore.WriteAllTextAtomic(_path, body);
        LastError = null;
    }

    /// <summary>
    /// The file's body, or null when there is none to read. A read that fails is remembered
    /// and reported as no body, so the caller decides what an unreadable file means.
    /// </summary>
    private string? ReadBody()
    {
        _readFailure = null;

        try
        {
            return File.Exists(_path) ? File.ReadAllText(_path) : null;
        }
        catch (IOException exception)
        {
            _readFailure = $"输入后端设置无法读取：{exception.Message}";
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            _readFailure = $"输入后端设置无法读取：{exception.Message}";
            return null;
        }
    }

    private string? _readFailure;

    /// <summary>
    /// Parses one body into the backend it describes. A body that is not JSON at all, or
    /// that names a backend this build does not know, is the platform default with the
    /// reason attached - the same damaged-file rule the foreground rules follow. Property
    /// names match case-insensitively, so a file a user hand-edits still reads.
    /// </summary>
    private static (InputBackend Backend, string? Problem) ReadBackend(string body)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredBackend>(body, ReadOptions);
            return InputBackendText.Parse(stored?.Backend);
        }
        catch (JsonException exception)
        {
            return (InputBackend.Platform, $"输入后端设置无法解析，已按平台默认读取：{exception.Message}");
        }
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class StoredBackend
    {
        public string? Backend { get; init; }
    }
}
