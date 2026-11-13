using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Services;

/// <summary>
/// The foreground rule table on disk: one JSON file beside the profiles, never inside the
/// profiles directory - <see cref="FileProfileStore"/> enumerates that directory, and a rules
/// file in it would be read back as a device's profile.
/// <para>
/// A missing file is the empty table, which is the state every existing install starts from: a
/// build that knows nothing about this feature keeps mapping exactly as it did. A damaged file
/// costs the rules and nothing else - the desktop default profile runs - and the reason is kept
/// for the panel to show.
/// </para>
/// <para>
/// One file, two writers' worth of readers: the GUI writes it on 保存, and the resident daemon -
/// which owns mapping and has no control-channel verb for a rule edit - re-reads it through
/// <see cref="LoadIfChanged"/>, so a save from any window reaches the mapping authority without
/// a restart.
/// </para>
/// </summary>
public sealed class ForegroundRuleStore
{
    public const string FileName = "foreground-rules.json";

    private readonly string _path;

    /// <summary>
    /// Why the last read could not be made, or null when it could. Kept beside
    /// <see cref="_lastBody"/> rather than folded into it, because "no file" and "file
    /// unreadable" are different answers: the first is the empty table, the second is the last
    /// table still in force.
    /// </summary>
    private string? _readFailure;

    /// <summary>
    /// The file body the last successful read saw, or null when it saw no file. The change
    /// signal for <see cref="LoadIfChanged"/>: compared as text rather than by timestamp,
    /// because a save that rewrites the same bytes is not a change and a timestamp cannot say
    /// so, and because the write is not atomic - a body read mid-write is a change the next
    /// poll repairs, not a table to be quietly dropped.
    /// </summary>
    private string? _lastBody;

    public ForegroundRuleStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeziroJoy",
            FileName);
    }

    /// <summary>Why the last read fell back to the defaults, or null when the file read whole.</summary>
    public string? LastError { get; private set; }

    public string FilePath => _path;

    public ForegroundSettings Load()
    {
        var body = ReadBody();

        if (body is null)
        {
            // No file is the empty table every install starts from, and says nothing; an
            // unreadable one is a failure worth quoting.
            LastError = _readFailure;
            Remember(null);
            return new ForegroundSettings();
        }

        return Apply(body);
    }

    /// <summary>
    /// Reads the table only when the file on disk differs from the last read, returning null
    /// when it does not - the daemon's poll path. A body that has not changed costs one file
    /// read and no parse, so a poll with nothing to do is as cheap as it is against a live
    /// engine. A body that cannot be read at all - a save landing mid-write, a permission
    /// change - keeps the last table in force rather than dropping to the desktop default on a
    /// transient error; the next poll tries again.
    /// </summary>
    public ForegroundSettings? LoadIfChanged()
    {
        var body = ReadBody();

        if (_readFailure is not null)
        {
            return null;
        }

        if (body is null)
        {
            if (_lastBody is null)
            {
                // Still no file, and none before it: nothing has changed.
                return null;
            }

            // The file is gone. That is a change: the empty table takes over, exactly as a
            // first read of an absent file reads.
            LastError = null;
            Remember(null);
            return new ForegroundSettings();
        }

        return string.Equals(body, _lastBody, StringComparison.Ordinal) ? null : Apply(body);
    }

    public void Save(ForegroundSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var body = ForegroundSettingsSerializer.Serialize(settings);
        File.WriteAllText(_path, body);
        LastError = null;
        Remember(body);
    }

    /// <summary>Removes the table, which is the same as an empty one.</summary>
    public bool Delete()
    {
        if (!File.Exists(_path))
        {
            return false;
        }

        File.Delete(_path);
        LastError = null;

        // The remembered body is deliberately left alone: it is what tells the next
        // <see cref="LoadIfChanged"/> that the file it last read is gone, which is the change
        // the empty table arrives on.
        return true;
    }

    /// <summary>
    /// The file's body, or null when there is none to read. A read that fails is remembered in
    /// <see cref="_readFailure"/> and reported as no body, so the caller decides what an
    /// unreadable file means - <see cref="Load"/> quotes it, <see cref="LoadIfChanged"/> keeps
    /// the last table.
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
            _readFailure = $"前台规则文件无法读取：{exception.Message}";
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            _readFailure = $"前台规则文件无法读取：{exception.Message}";
            return null;
        }
    }

    /// <summary>Parses one body into the table it describes and remembers it as the last read.</summary>
    private ForegroundSettings Apply(string body)
    {
        var (settings, problems) = ForegroundSettingsSerializer.Load(body);
        LastError = problems.Count > 0 ? problems[0] : null;
        Remember(body);

        return settings;
    }

    /// <summary>Records the body the table now in force came from, or null when there is no file.</summary>
    private void Remember(string? body) => _lastBody = body;
}
