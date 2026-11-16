using DeziroJoy.Platform.Windows.Gaming;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

/// <summary>
/// The two Game Bar popup layers, against an in-memory registry: the per-user preference,
/// and the per-user no-op ms-gamebar handler that answers the link when the preference
/// alone does not. The handler tests are the round trip that matters on a real machine -
/// register, read back, restore, and end up with exactly the state the system started in.
/// </summary>
public sealed class GameBarPopupGuardTests
{
    private const string ProtocolKeyPath = @"Software\Classes\ms-gamebar";
    private const string CommandKeyPath = ProtocolKeyPath + @"\shell\open\command";
    private const string PreferenceKeyPath = @"Software\Microsoft\GameBar";
    private const string Executable = @"C:\Users\Test\AppData\Local\Programs\DeziroJoy\DeziroJoy.exe";

    private static GameBarPopupGuard Guard(FakeGameBarRegistry registry, string? executable = Executable)
        => new(() => true, registry, () => executable);

    [Fact]
    public void Unsupported_Platform_Neither_Reads_Nor_Writes()
    {
        var registry = new FakeGameBarRegistry();
        var guard = new GameBarPopupGuard(() => false, registry, () => Executable);

        Assert.False(guard.IsSupported);
        Assert.False(guard.IsControllerLaunchDisabled);
        Assert.False(guard.OwnsProtocolHandler);
        Assert.False(guard.IsProtocolHandlerRegistered);
        Assert.Throws<PlatformNotSupportedException>(guard.DisableControllerLaunch);
        Assert.Throws<PlatformNotSupportedException>(guard.RegisterProtocolHandler);
        Assert.Throws<PlatformNotSupportedException>(guard.RestoreProtocolHandler);
        Assert.Empty(registry.Keys);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void Controller_Launch_State_Reads_The_Current_User_Setting(int? value, bool disabled)
    {
        var registry = new FakeGameBarRegistry();
        if (value is not null)
        {
            registry.WriteDWord(PreferenceKeyPath, "UseNexusForGameBarEnabled", value.Value);
        }

        var guard = Guard(registry);

        Assert.True(guard.IsSupported);
        Assert.Equal(disabled, guard.IsControllerLaunchDisabled);
    }

    [Fact]
    public void Disable_Controller_Launch_Writes_And_Verifies_The_Dedicated_Setting()
    {
        var registry = new FakeGameBarRegistry();
        registry.WriteDWord(PreferenceKeyPath, "UseNexusForGameBarEnabled", 1);
        var guard = Guard(registry);

        guard.DisableControllerLaunch();

        Assert.Equal(0, registry.ReadDWord(PreferenceKeyPath, "UseNexusForGameBarEnabled"));
        Assert.True(guard.IsControllerLaunchDisabled);
    }

    [Fact]
    public void Disable_Controller_Launch_Rejects_A_Write_That_Does_Not_Stick()
    {
        var registry = new FakeGameBarRegistry { IgnoreWrites = true };
        var guard = Guard(registry);

        Assert.Throws<InvalidOperationException>(guard.DisableControllerLaunch);
    }

    [Fact]
    public void Register_Writes_The_Quoted_NoOp_Command_And_Marks_Ownership()
    {
        var registry = new FakeGameBarRegistry();
        var guard = Guard(registry);

        guard.RegisterProtocolHandler();

        Assert.Equal(
            $"\"{Executable}\" --noop-gamebar \"%1\"",
            registry.ReadString(CommandKeyPath, null));
        Assert.Equal(1, registry.ReadDWord(ProtocolKeyPath, "DeziroJoyOwner"));
        Assert.True(guard.OwnsProtocolHandler);
        Assert.True(guard.IsProtocolHandlerRegistered);
    }

    [Fact]
    public void Register_Under_The_DotNet_Host_Is_Refused()
    {
        // "dotnet run" would register the muxer, which never starts the app.
        var registry = new FakeGameBarRegistry();
        var guard = Guard(registry, executable: null);

        Assert.Throws<InvalidOperationException>(guard.RegisterProtocolHandler);
        Assert.Empty(registry.Keys);
    }

    [Fact]
    public void Register_Over_A_Foreign_Handler_Remembers_What_Was_There()
    {
        var registry = new FakeGameBarRegistry();
        registry.WriteString(
            CommandKeyPath,
            null,
            "\"C:\\Program Files\\Xbox\\XboxGameBar.exe\" \"%1\"");
        var guard = Guard(registry);

        guard.RegisterProtocolHandler();

        Assert.Equal(
            "\"C:\\Program Files\\Xbox\\XboxGameBar.exe\" \"%1\"",
            registry.ReadString(ProtocolKeyPath, "DeziroJoyPreviousCommand"));
    }

    [Fact]
    public void Register_Again_After_Losing_Only_The_Marker_Does_Not_Back_Up_Our_Own_Command()
    {
        var registry = new FakeGameBarRegistry();
        var guard = Guard(registry);
        guard.RegisterProtocolHandler();
        registry.DeleteValue(ProtocolKeyPath, "DeziroJoyOwner");

        guard.RegisterProtocolHandler();

        Assert.Null(registry.ReadString(ProtocolKeyPath, "DeziroJoyPreviousCommand"));
        Assert.True(guard.IsProtocolHandlerRegistered);
    }

    [Fact]
    public void Register_Keeps_The_First_Backup_When_Already_Owned()
    {
        var registry = new FakeGameBarRegistry();
        registry.WriteString(CommandKeyPath, null, "\"C:\\old\\handler.exe\" \"%1\"");
        var guard = Guard(registry);
        guard.RegisterProtocolHandler();

        guard.RegisterProtocolHandler();

        Assert.Equal(
            "\"C:\\old\\handler.exe\" \"%1\"",
            registry.ReadString(ProtocolKeyPath, "DeziroJoyPreviousCommand"));
    }

    [Fact]
    public void A_Registration_That_Names_Another_Path_Is_Stale_But_Still_Ours()
    {
        var registry = new FakeGameBarRegistry();
        Guard(registry).RegisterProtocolHandler();

        // The app moved: a new executable now owns the stale registration.
        var moved = Guard(registry, executable: @"D:\DeziroJoy\DeziroJoy.exe");

        Assert.True(moved.OwnsProtocolHandler);
        Assert.False(moved.IsProtocolHandlerRegistered);

        moved.RegisterProtocolHandler();

        Assert.Equal(
            "\"D:\\DeziroJoy\\DeziroJoy.exe\" --noop-gamebar \"%1\"",
            registry.ReadString(CommandKeyPath, null));
        Assert.True(moved.IsProtocolHandlerRegistered);
    }

    [Fact]
    public void Restore_Still_Works_On_A_Stale_Our_Own_Handler()
    {
        // A moved exe leaves the command ours, only the path stale - restoring it is still
        // taking back exactly what this app registered.
        var registry = new FakeGameBarRegistry();
        Guard(registry).RegisterProtocolHandler();
        var moved = Guard(registry, executable: @"D:\DeziroJoy\DeziroJoy.exe");

        moved.RestoreProtocolHandler();

        Assert.False(registry.KeyExists(ProtocolKeyPath));
        Assert.False(moved.OwnsProtocolHandler);
    }

    [Fact]
    public void A_Foreign_Command_Under_The_Marker_Is_A_Takeover_Not_Ownership()
    {
        var registry = new FakeGameBarRegistry();
        var guard = Guard(registry);
        guard.RegisterProtocolHandler();

        // Another program replaced the command and never learned about our private marker.
        registry.WriteString(CommandKeyPath, null, "\"C:\\Xbox\\XboxGameBar.exe\" \"%1\"");

        Assert.True(registry.ReadDWord(ProtocolKeyPath, "DeziroJoyOwner").HasValue);
        Assert.False(guard.OwnsProtocolHandler);
        Assert.True(guard.IsProtocolHandlerTakenOver);
        Assert.False(guard.IsProtocolHandlerRegistered);
    }

    [Fact]
    public void Restore_Refuses_When_A_Foreign_Command_Holds_The_Protocol()
    {
        var registry = new FakeGameBarRegistry();
        var guard = Guard(registry);
        guard.RegisterProtocolHandler();
        registry.WriteString(CommandKeyPath, null, "\"C:\\Xbox\\XboxGameBar.exe\" \"%1\"");

        Assert.Throws<InvalidOperationException>(guard.RestoreProtocolHandler);

        // Their command survives untouched, and nothing new was recorded or deleted.
        Assert.Equal(
            "\"C:\\Xbox\\XboxGameBar.exe\" \"%1\"",
            registry.ReadString(CommandKeyPath, null));
        Assert.Equal(1, registry.ReadDWord(ProtocolKeyPath, "DeziroJoyOwner"));
        Assert.Null(registry.ReadString(ProtocolKeyPath, "DeziroJoyPreviousCommand"));
    }

    [Fact]
    public void Restore_Refuses_When_A_Foreign_Command_Holds_The_Protocol_With_A_Backup()
    {
        var registry = new FakeGameBarRegistry();
        registry.WriteString(CommandKeyPath, null, "\"C:\\old\\handler.exe\" \"%1\"");
        var guard = Guard(registry);
        guard.RegisterProtocolHandler();

        // Takeover of an already-backed-up registration: the backup must survive verbatim,
        // because overwriting it with the foreign command would lose the real default.
        registry.WriteString(CommandKeyPath, null, "\"C:\\Xbox\\XboxGameBar.exe\" \"%1\"");
        Assert.Throws<InvalidOperationException>(guard.RestoreProtocolHandler);

        Assert.Equal(
            "\"C:\\Xbox\\XboxGameBar.exe\" \"%1\"",
            registry.ReadString(CommandKeyPath, null));
        Assert.Equal(
            "\"C:\\old\\handler.exe\" \"%1\"",
            registry.ReadString(ProtocolKeyPath, "DeziroJoyPreviousCommand"));
        Assert.Equal(1, registry.ReadDWord(ProtocolKeyPath, "DeziroJoyOwner"));
    }

    [Fact]
    public void Register_Over_A_Takeover_Records_The_Foreign_Command_When_No_Backup_Exists()
    {
        var registry = new FakeGameBarRegistry();
        var guard = Guard(registry);
        guard.RegisterProtocolHandler();
        registry.WriteString(CommandKeyPath, null, "\"C:\\Xbox\\XboxGameBar.exe\" \"%1\"");

        // The power-user path (the CLI install) takes the protocol back from the foreign
        // handler, recording it first so a later restore hands it over intact.
        guard.RegisterProtocolHandler();

        Assert.Equal(
            "\"C:\\Xbox\\XboxGameBar.exe\" \"%1\"",
            registry.ReadString(ProtocolKeyPath, "DeziroJoyPreviousCommand"));
        Assert.True(guard.IsProtocolHandlerRegistered);

        guard.RestoreProtocolHandler();

        Assert.Equal(
            "\"C:\\Xbox\\XboxGameBar.exe\" \"%1\"",
            registry.ReadString(CommandKeyPath, null));
    }

    [Fact]
    public void Register_Over_A_Takeover_Keeps_The_First_Backup()
    {
        var registry = new FakeGameBarRegistry();
        registry.WriteString(CommandKeyPath, null, "\"C:\\old\\handler.exe\" \"%1\"");
        var guard = Guard(registry);
        guard.RegisterProtocolHandler();
        registry.WriteString(CommandKeyPath, null, "\"C:\\Xbox\\XboxGameBar.exe\" \"%1\"");

        guard.RegisterProtocolHandler();

        Assert.Equal(
            "\"C:\\old\\handler.exe\" \"%1\"",
            registry.ReadString(ProtocolKeyPath, "DeziroJoyPreviousCommand"));
    }

    [Fact]
    public void Restore_Puts_Back_The_Previous_Command_And_Drops_Ownership()
    {
        var registry = new FakeGameBarRegistry();
        registry.WriteString(CommandKeyPath, null, "\"C:\\old\\handler.exe\" \"%1\"");
        var guard = Guard(registry);
        guard.RegisterProtocolHandler();

        guard.RestoreProtocolHandler();

        Assert.Equal("\"C:\\old\\handler.exe\" \"%1\"", registry.ReadString(CommandKeyPath, null));
        Assert.Null(registry.ReadDWord(ProtocolKeyPath, "DeziroJoyOwner"));
        Assert.Null(registry.ReadString(ProtocolKeyPath, "DeziroJoyPreviousCommand"));
        Assert.False(guard.OwnsProtocolHandler);
        Assert.False(guard.IsProtocolHandlerRegistered);
    }

    [Fact]
    public void Restore_Removes_The_Key_When_Nothing_Was_There_Before()
    {
        var registry = new FakeGameBarRegistry();
        var guard = Guard(registry);
        guard.RegisterProtocolHandler();

        guard.RestoreProtocolHandler();

        Assert.False(registry.SubKeyExists(@"Software\Classes", "ms-gamebar"));
        Assert.False(registry.KeyExists(ProtocolKeyPath));
        Assert.False(guard.OwnsProtocolHandler);
        Assert.False(guard.IsProtocolHandlerRegistered);
    }

    [Fact]
    public void Restore_Keeps_A_Handler_Another_Owner_Has_Taken_Over()
    {
        var registry = new FakeGameBarRegistry();
        registry.WriteString(CommandKeyPath, null, "\"C:\\someone\\else.exe\" \"%1\"");
        var guard = Guard(registry);

        guard.RestoreProtocolHandler();

        Assert.Equal("\"C:\\someone\\else.exe\" \"%1\"", registry.ReadString(CommandKeyPath, null));
        Assert.Null(registry.ReadDWord(ProtocolKeyPath, "DeziroJoyOwner"));
        Assert.False(guard.OwnsProtocolHandler);
    }

    [Fact]
    public void Restore_Keeps_Unrelated_Values_And_SubKeys_Under_The_Protocol()
    {
        var registry = new FakeGameBarRegistry();
        registry.WriteString(ProtocolKeyPath, "SomeOtherValue", "keep me");
        var guard = Guard(registry);
        guard.RegisterProtocolHandler();

        guard.RestoreProtocolHandler();

        Assert.Equal("keep me", registry.ReadString(ProtocolKeyPath, "SomeOtherValue"));
        Assert.True(registry.KeyExists(ProtocolKeyPath));
        Assert.False(registry.SubKeyExists(ProtocolKeyPath, "shell"));
    }

    [Fact]
    public void Restore_Is_Idempotent()
    {
        var registry = new FakeGameBarRegistry();
        var guard = Guard(registry);
        guard.RegisterProtocolHandler();
        guard.RestoreProtocolHandler();

        guard.RestoreProtocolHandler();

        Assert.False(registry.KeyExists(ProtocolKeyPath));
        Assert.False(guard.OwnsProtocolHandler);
    }

    [Fact]
    public void Register_Then_Restore_Returns_The_Registry_To_Its_Original_State()
    {
        var registry = new FakeGameBarRegistry();
        var guard = Guard(registry);
        guard.RegisterProtocolHandler();

        guard.RestoreProtocolHandler();

        // The whole protocol tree - key, values and subkeys - is gone; empty parents above
        // it stay behind, exactly as the real registry leaves them.
        Assert.DoesNotContain(
            registry.Keys,
            key => key.StartsWith(ProtocolKeyPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_Registered_Handler_Survives_A_Refresh_And_Clears_After_Restore()
    {
        var registry = new FakeGameBarRegistry();
        var guard = Guard(registry);
        Assert.False(guard.IsProtocolHandlerRegistered);

        guard.RegisterProtocolHandler();
        Assert.True(Guard(registry).IsProtocolHandlerRegistered);

        guard.RestoreProtocolHandler();
        Assert.False(Guard(registry).IsProtocolHandlerRegistered);
    }
}

/// <summary>
/// An in-memory HKCU slice that creates intermediate keys on write, like the real registry
/// does, so the restore path's pruning of the keys it created is exercised for real.
/// </summary>
internal sealed class FakeGameBarRegistry : IGameBarRegistry
{
    private readonly Dictionary<string, Dictionary<string, object?>> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Simulates a write that does not stick, so the verify-after-write paths fire.</summary>
    public bool IgnoreWrites { get; set; }

    public IReadOnlyCollection<string> Keys => _keys.Keys;

    public bool KeyExists(string keyPath) => _keys.ContainsKey(keyPath);

    public int? ReadDWord(string keyPath, string? valueName)
        => Read(keyPath, valueName) is int value ? value : null;

    public string? ReadString(string keyPath, string? valueName)
        => Read(keyPath, valueName) as string;

    public void WriteDWord(string keyPath, string? valueName, int value)
        => Write(keyPath, valueName, value);

    public void WriteString(string keyPath, string? valueName, string value)
        => Write(keyPath, valueName, value);

    public void DeleteValue(string keyPath, string? valueName)
    {
        if (!IgnoreWrites && _keys.TryGetValue(keyPath, out var values))
        {
            values.Remove(Name(valueName));
        }
    }

    public void DeleteSubKeyTree(string keyPath, string subKeyPath)
    {
        if (IgnoreWrites)
        {
            return;
        }

        var prefix = keyPath + "\\" + subKeyPath;
        foreach (var key in _keys.Keys
            .Where(key => key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
            .ToArray())
        {
            _keys.Remove(key);
        }
    }

    public bool SubKeyExists(string keyPath, string subKeyPath)
        => _keys.ContainsKey(keyPath + "\\" + subKeyPath);

    public bool IsEmptyKey(string keyPath)
        => (!_keys.TryGetValue(keyPath, out var values) || values.Count == 0)
            && !_keys.Keys.Any(key =>
                key.StartsWith(keyPath + "\\", StringComparison.OrdinalIgnoreCase));

    public void DeleteKey(string keyPath)
    {
        if (IgnoreWrites)
        {
            return;
        }

        var prefix = keyPath + "\\";
        foreach (var key in _keys.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray())
        {
            _keys.Remove(key);
        }

        _keys.Remove(keyPath);
    }

    private object? Read(string keyPath, string? valueName)
        => _keys.TryGetValue(keyPath, out var values)
            && values.TryGetValue(Name(valueName), out var value)
                ? value
                : null;

    private void Write(string keyPath, string? valueName, object value)
    {
        if (IgnoreWrites)
        {
            return;
        }

        EnsureKey(keyPath);
        _keys[keyPath][Name(valueName)] = value;
    }

    private void EnsureKey(string keyPath)
    {
        var path = string.Empty;
        foreach (var part in keyPath.Split('\\'))
        {
            path = path.Length == 0 ? part : path + "\\" + part;
            if (!_keys.ContainsKey(path))
            {
                _keys[path] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static string Name(string? valueName) => valueName ?? string.Empty;
}
