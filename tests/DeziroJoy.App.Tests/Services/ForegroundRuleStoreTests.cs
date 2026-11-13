using DeziroJoy.App.Services;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The rule table on disk. The file is new, so what matters is the opposite of backward
/// compatibility: a table written now must read back exactly, a missing file must read as the
/// empty table an existing install starts from, and a damaged one must cost the rules and
/// nothing else.
/// </summary>
public sealed class ForegroundRuleStoreTests : IDisposable
{
    private readonly string _directory;

    public ForegroundRuleStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-foreground-rules", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private ForegroundRuleStore Store() => new(Path.Combine(_directory, ForegroundRuleStore.FileName));

    [Fact]
    public void A_Missing_File_Reads_As_The_Empty_Table()
    {
        var store = Store();

        var settings = store.Load();

        Assert.Empty(settings.Rules);
        Assert.Equal(ForegroundRule.DefaultSlot, settings.DefaultSlot);
        Assert.True(settings.Enabled);
        Assert.Null(store.LastError);
    }

    [Fact]
    public void A_Table_Round_Trips_Through_The_Store()
    {
        var store = Store();
        var saved = new ForegroundSettings
        {
            DefaultSlot = 3,
            Enabled = true,
            Rules =
            [
                new ForegroundRule
                {
                    ProcessName = "Chrome.exe",
                    Priority = 5,
                    Action = ForegroundAction.Profile,
                    Slot = 2,
                    FullscreenOnly = true,
                },
                new ForegroundRule
                {
                    ProcessName = "game",
                    Action = ForegroundAction.NativeGamepad,
                },
            ],
        };

        store.Save(saved);
        var loaded = store.Load();

        Assert.Equal(3, loaded.DefaultSlot);
        Assert.Equal(2, loaded.Rules.Count);
        Assert.Equal("Chrome.exe", loaded.Rules[0].ProcessName);
        Assert.True(loaded.Rules[0].FullscreenOnly);
        Assert.Equal(ForegroundAction.NativeGamepad, loaded.Rules[1].Action);
        Assert.Null(store.LastError);
    }

    [Fact]
    public void A_Damaged_File_Costs_The_Rules_And_Says_Why()
    {
        var store = Store();
        File.WriteAllText(Path.Combine(_directory, ForegroundRuleStore.FileName), "{ not json at all");

        var settings = store.Load();

        Assert.Empty(settings.Rules);
        Assert.NotNull(store.LastError);
    }

    [Fact]
    public void The_Table_Lives_Beside_The_Profiles_Never_Inside_Them()
    {
        // FileProfileStore enumerates the profiles directory, so a rules file in it would be
        // read back as a device's profile.
        var store = Store();
        var profileDirectory = Path.Combine(_directory, "profiles");
        Directory.CreateDirectory(profileDirectory);

        store.Save(new ForegroundSettings { Rules = [new ForegroundRule { ProcessName = "chrome" }] });

        Assert.Empty(Directory.EnumerateFiles(profileDirectory));
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public void Deleting_The_Table_Leaves_The_Empty_One_Behind()
    {
        var store = Store();
        store.Save(new ForegroundSettings { Rules = [new ForegroundRule { ProcessName = "chrome" }] });

        Assert.True(store.Delete());
        Assert.False(store.Delete());
        Assert.Empty(store.Load().Rules);
    }

    [Fact]
    public void Saving_Creates_The_Directory_It_Needs()
    {
        var store = new ForegroundRuleStore(Path.Combine(_directory, "nested", "deeper", ForegroundRuleStore.FileName));

        store.Save(new ForegroundSettings());

        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void LoadIfChanged_Reads_Nothing_While_The_File_Holds_Still()
    {
        var store = Store();
        store.Save(new ForegroundSettings { Rules = [new ForegroundRule { ProcessName = "chrome" }] });

        // The save is the baseline - the writer already knows what it wrote - so the daemon's
        // poll must not re-apply a table that is already the one in force.
        Assert.Null(store.LoadIfChanged());
        Assert.Null(store.LoadIfChanged());
        Assert.Null(store.LoadIfChanged());
    }

    [Fact]
    public void Rewriting_The_Same_Table_Is_Not_A_Change()
    {
        var store = Store();
        var table = new ForegroundSettings { Rules = [new ForegroundRule { ProcessName = "chrome" }] };
        store.Save(table);
        Assert.Null(store.LoadIfChanged());

        File.WriteAllText(
            Path.Combine(_directory, ForegroundRuleStore.FileName),
            ForegroundSettingsSerializer.Serialize(table));

        Assert.Null(store.LoadIfChanged());
    }

    [Fact]
    public void LoadIfChanged_Sees_Another_Writers_Save_As_A_Change()
    {
        // The daemon reads what a window in another process wrote, so the writer is a second
        // store over the same file.
        var reader = Store();
        var writer = Store();

        Assert.Null(reader.LoadIfChanged());

        writer.Save(new ForegroundSettings { Rules = [new ForegroundRule { ProcessName = "game", Slot = 3 }] });

        var settings = reader.LoadIfChanged();
        Assert.NotNull(settings);
        Assert.Equal(3, settings!.Rules.Single().Slot);
        Assert.Null(reader.LoadIfChanged());
    }

    [Fact]
    public void LoadIfChanged_Sees_An_Outside_Write_As_A_Change()
    {
        // The GUI is a different process from the daemon, so an edit is an outside write.
        var store = Store();
        File.WriteAllText(
            Path.Combine(_directory, ForegroundRuleStore.FileName),
            ForegroundSettingsSerializer.Serialize(new ForegroundSettings { DefaultSlot = 4 }));

        var settings = store.LoadIfChanged();

        Assert.NotNull(settings);
        Assert.Equal(4, settings!.DefaultSlot);
    }

    [Fact]
    public void Deleting_The_File_Changes_The_Table_To_The_Empty_One()
    {
        var store = Store();
        store.Save(new ForegroundSettings { Rules = [new ForegroundRule { ProcessName = "chrome" }] });
        Assert.Null(store.LoadIfChanged());

        Assert.True(store.Delete());

        var settings = store.LoadIfChanged();
        Assert.NotNull(settings);
        Assert.Empty(settings!.Rules);
        Assert.Null(store.LoadIfChanged());
    }

    [Fact]
    public void A_Damaged_File_Is_A_Change_That_Costs_The_Rules_And_Says_Why()
    {
        var store = Store();
        store.Save(new ForegroundSettings { Rules = [new ForegroundRule { ProcessName = "chrome" }] });
        Assert.Null(store.LoadIfChanged());

        File.WriteAllText(Path.Combine(_directory, ForegroundRuleStore.FileName), "{ not json at all");

        var settings = store.LoadIfChanged();

        Assert.NotNull(settings);
        Assert.Empty(settings!.Rules);
        Assert.NotNull(store.LastError);
    }
}
