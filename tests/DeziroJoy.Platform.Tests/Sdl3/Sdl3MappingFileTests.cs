using DeziroJoy.Platform.Sdl3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// The persisted mapping file: serialization, storage, loading and the diagnostics for a file a
/// human broke. Deterministic by construction - every case is text in and text out over a
/// temporary path, with no SDL and no device.
/// </summary>
public sealed class Sdl3MappingFileTests : IDisposable
{
    private readonly string _directory;

    public Sdl3MappingFileTests()
    {
        _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dezirojoy-sdl-mapping-" + Guid.NewGuid().ToString("N"));
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
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string PathFor(string name = "sdl-mappings.json")
        => System.IO.Path.Combine(_directory, name);

    private static Sdl3MappingEntry Entry(
        string guid = "03000000790000000006000000000000",
        string name = "Generic USB Joystick",
        string? mapping = null,
        string? updated = "2026-11-16T00:00:00.0000000Z")
        => new()
        {
            GuidString = guid,
            DeviceName = name,
            Mapping = mapping ?? $"{guid},{name},platform:Linux,a:b0,b:b1,x:b2,start:b7,leftx:a0,lefty:a1",
            UpdatedUtc = updated!,
        };

    [Fact]
    public void A_Missing_File_Is_An_Empty_Snapshot_With_No_Problem()
    {
        var snapshot = Sdl3MappingFile.Load(PathFor("absent.json"));

        Assert.Empty(snapshot.Mappings);
        Assert.Null(snapshot.Problem);
    }

    [Fact]
    public void A_Saved_Entry_Round_Trips_Through_Load()
    {
        var path = PathFor();
        var entry = Entry();

        var saved = Sdl3MappingFile.Save(path, entry);

        Assert.True(saved.Saved);
        Assert.True(File.Exists(path));

        var loaded = Sdl3MappingFile.Load(path);
        Assert.Null(loaded.Problem);
        var round = Assert.Single(loaded.Mappings);
        Assert.Equal(entry.GuidString, round.GuidString);
        Assert.Equal(entry.DeviceName, round.DeviceName);
        Assert.Equal(entry.Mapping, round.Mapping);
        Assert.Equal(entry.UpdatedUtc, round.UpdatedUtc);
    }

    [Fact]
    public void Saving_The_Same_Guid_Updates_Its_Entry_Instead_Of_Stacking_A_Second_One()
    {
        var path = PathFor();
        Assert.True(Sdl3MappingFile.Save(path, Entry()).Saved);

        var updated = Entry(
            mapping: "03000000790000000006000000000000,Generic USB Joystick,platform:Linux,a:b0",
            updated: "2026-11-17T00:00:00.0000000Z");
        var result = Sdl3MappingFile.Save(path, updated);

        Assert.True(result.Saved);

        var loaded = Sdl3MappingFile.Load(path);
        var single = Assert.Single(loaded.Mappings);
        Assert.Equal(updated.Mapping, single.Mapping);
        Assert.Equal("2026-11-17T00:00:00.0000000Z", single.UpdatedUtc);
    }

    [Fact]
    public void Saving_A_Second_Device_Preserves_The_First()
    {
        var path = PathFor();
        Assert.True(Sdl3MappingFile.Save(path, Entry()).Saved);
        Assert.True(Sdl3MappingFile.Save(
            path,
            Entry(guid: "03000000deaded000002000000000000", name: "Another Pad")).Saved);

        var loaded = Sdl3MappingFile.Load(path);

        Assert.Equal(2, loaded.Mappings.Count);
        // Sorted by GUID, so the file's bytes are a function of its contents alone.
        Assert.Equal(
            ["03000000790000000006000000000000", "03000000deaded000002000000000000"],
            loaded.Mappings.Select(entry => entry.GuidString));
    }

    [Fact]
    public void Saving_Leaves_No_Temporary_File_Behind()
    {
        var path = PathFor();
        Assert.True(Sdl3MappingFile.Save(path, Entry()).Saved);

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void A_Body_That_Is_Not_Json_Reports_The_Problem_And_Yields_Nothing()
    {
        var path = PathFor();
        File.WriteAllText(path, "this is not json");

        var snapshot = Sdl3MappingFile.Load(path);

        Assert.Empty(snapshot.Mappings);
        Assert.Contains("无法解析", snapshot.Problem);
    }

    [Fact]
    public void An_Unreadable_File_Reports_The_Reason_And_Refuses_To_Overwrite_It()
    {
        var path = PathFor();
        File.WriteAllText(path, "{ broken");

        var result = Sdl3MappingFile.Save(path, Entry());

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, problem => problem.Contains("无法解析"));
        // The broken body is untouched: the user repairs the file instead of losing the entries
        // this build cannot see.
        Assert.Equal("{ broken", File.ReadAllText(path));
    }

    [Fact]
    public void Duplicate_GUIDs_In_A_Hand_Edited_File_Resolve_To_The_Last_One()
    {
        var path = PathFor();
        var guid = "03000000790000000006000000000000";
        File.WriteAllText(path, $$"""
        {
          "version": 1,
          "mappings": [
            { "guid": "{{guid}}", "name": "First", "mapping": "{{guid}},First,platform:Linux,a:b0", "updatedUtc": "2026-01-01T00:00:00.0000000Z" },
            { "guid": "{{guid}}", "name": "Second", "mapping": "{{guid}},Second,platform:Linux,a:b1", "updatedUtc": "2026-02-01T00:00:00.0000000Z" }
          ]
        }
        """);

        var single = Assert.Single(Sdl3MappingFile.Load(path).Mappings);

        Assert.Equal("Second", single.DeviceName);
    }

    [Theory]
    [InlineData("not-a-guid,Generic USB Joystick,platform:Linux,a:b0")]      // bad GUID
    [InlineData("03000000790000000006000000000000,,platform:Linux,a:b0")]   // empty name
    [InlineData("03000000790000000006000000000000,Pad")]                    // no binding
    [InlineData("03000000790000000006000000000000,Pad,platform:Linux,a:x9")]     // not a control token
    [InlineData("03000000790000000006000000000000,Pad,platform:Linux,a")]         // missing value
    [InlineData("03000000790000000006000000000000,Pad,platform:Linux,a:")]        // empty value
    [InlineData("03000000790000000006000000000000,Pad,platform:Linux,:b0")]       // empty key
    public void Validate_Reports_What_Is_Wrong_With_A_Mapping(string mapping)
    {
        var entry = new Sdl3MappingEntry
        {
            GuidString = "03000000790000000006000000000000",
            DeviceName = "Generic USB Joystick",
            Mapping = mapping,
            UpdatedUtc = "2026-11-16T00:00:00.0000000Z",
        };

        Assert.NotEmpty(Sdl3MappingFile.Validate(entry));
    }

    [Theory]
    [InlineData("south:b0")]
    [InlineData("east:b1")]
    [InlineData("west:b2")]
    [InlineData("north:b3")]
    public void Validate_Names_The_Positional_Face_Keys_Sdl_Silently_Drops(string element)
    {
        // SDL's parser matches its key table exactly and drops an element it does not recognize
        // *without an error*, so a mapping written with the enum's positional names loads,
        // applies and has no face buttons at all. The validator has to say so, because nothing
        // else will.
        var entry = new Sdl3MappingEntry
        {
            GuidString = "03000000790000000006000000000000",
            DeviceName = "Pad",
            Mapping = $"03000000790000000006000000000000,Pad,platform:Linux,{element}",
            UpdatedUtc = "2026-11-16T00:00:00.0000000Z",
        };

        var problems = Sdl3MappingFile.Validate(entry);

        var problem = Assert.Single(problems);
        Assert.Contains("静默忽略", problem);
        Assert.Contains("a／b／x／y", problem);
    }

    [Fact]
    public void Loading_Reports_Entries_Sdl_Will_Not_Honour_Without_Dropping_Them()
    {
        // The entry stays applied - the parts of it that do work keep working - and the problem
        // is reported so the daemon can name the device that is only partly mapped.
        var path = PathFor();
        var guid = "03000000790000000006000000000000";
        File.WriteAllText(path, "{\n" +
            "  \"version\": 1,\n" +
            "  \"mappings\": [\n" +
            "    { \"guid\": \"" + guid + "\", \"name\": \"Pad\", \"mapping\": \"" + guid + ",Pad,platform:Linux,south:b0,dpup:b1\", \"updatedUtc\": \"2026-11-16T00:00:00.0000000Z\" }\n" +
            "  ]\n" +
            "}\n");

        var snapshot = Sdl3MappingFile.Load(path);

        var single = Assert.Single(snapshot.Mappings);
        Assert.Contains("dpup:b1", single.Mapping);
        Assert.Contains(snapshot.Problems, problem => problem.Contains("静默忽略"));
    }

    [Fact]
    public void A_File_That_Reads_Whole_And_Validates_Reports_No_Problem()
    {
        var path = PathFor();
        Assert.True(Sdl3MappingFile.Save(path, Entry()).Saved);

        var snapshot = Sdl3MappingFile.Load(path);

        Assert.Null(snapshot.Problem);
        Assert.Empty(snapshot.Problems);
    }

    [Fact]
    public void Validate_Tolerates_The_Trailing_Comma_SDL_Itself_Tolerates()
    {
        var entry = new Sdl3MappingEntry
        {
            GuidString = "03000000790000000006000000000000",
            DeviceName = "Pad",
            Mapping = "03000000790000000006000000000000,Pad,platform:Linux,a:b0,",
            UpdatedUtc = "2026-11-16T00:00:00.0000000Z",
        };

        Assert.Empty(Sdl3MappingFile.Validate(entry));
    }

    [Fact]
    public void Validate_Accepts_The_Tokens_The_Capture_Flow_Produces()
    {
        var guid = "03000000790000000006000000000000";
        var mapping = string.Join(',',
            guid,
            "Generic USB Joystick",
            "platform:Linux",
            "a:b0",
            "b:b1",
            "dpup:h0.1",
            "dpdown:h0.4",
            "start:b7",
            "back:b6",
            "guide:b8",
            "leftshoulder:b4",
            "rightshoulder:b5",
            "leftstick:b9",
            "rightstick:b10",
            "leftx:a0",
            "lefty:a1",
            "rightx:a2",
            "righty:a3~",
            "lefttrigger:+a4",
            "righttrigger:-a5");

        var entry = new Sdl3MappingEntry
        {
            GuidString = guid,
            DeviceName = "Generic USB Joystick",
            Mapping = mapping,
            UpdatedUtc = "2026-11-16T00:00:00.0000000Z",
        };

        Assert.Empty(Sdl3MappingFile.Validate(entry));
        Assert.NotNull(Sdl3MappingFile.TryBuild(guid, "Generic USB Joystick", mapping));
    }

    [Fact]
    public void TryBuild_Returns_Null_For_A_Mapping_It_Cannot_Validate()
        => Assert.Null(Sdl3MappingFile.TryBuild("nope", "Pad", "nope,Pad,platform:Linux,a:b0"));

    [Fact]
    public void Saving_An_Invalid_Entry_Is_Refused_Before_The_File_Is_Touched()
    {
        var path = PathFor();
        Assert.True(Sdl3MappingFile.Save(path, Entry()).Saved);
        var before = File.ReadAllText(path);

        var result = Sdl3MappingFile.Save(path, Entry(mapping: "broken"));

        Assert.False(result.Saved);
        Assert.NotEmpty(result.Problems);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void The_Source_Reads_The_Path_It_Was_Given()
    {
        var path = PathFor();
        Assert.True(Sdl3MappingFile.Save(path, Entry()).Saved);

        var snapshot = new Sdl3MappingFileSource(path).Load();

        Assert.Single(snapshot.Mappings);
        Assert.Equal(path, new Sdl3MappingFileSource(path).Path);
    }

    [Fact]
    public void Entries_With_No_Mapping_Are_Skipped_Instead_Of_Failing_The_Whole_File()
    {
        var path = PathFor();
        File.WriteAllText(path, """
        {
          "version": 1,
          "mappings": [
            { "guid": "", "name": "empty", "mapping": "", "updatedUtc": "" },
            { "guid": "03000000790000000006000000000000", "name": "Pad", "mapping": "03000000790000000006000000000000,Pad,platform:Linux,a:b0", "updatedUtc": "2026-11-16T00:00:00.0000000Z" }
          ]
        }
        """);

        var single = Assert.Single(Sdl3MappingFile.Load(path).Mappings);

        Assert.Equal("03000000790000000006000000000000", single.GuidString);
    }

    [Fact]
    public void Describe_Lists_Every_Mapping_With_Its_Update_Stamp()
    {
        var lines = Sdl3MappingFileFormat.Describe(
            new Sdl3MappingSnapshot([Entry()], "示例问题"));

        Assert.Contains(lines, line => line.Contains("持久 SDL 映射：1 条"));
        Assert.Contains(lines, line => line.Contains("示例问题"));
        Assert.Contains(lines, line => line.Contains("2026-11-16T00:00:00.0000000Z"));
        Assert.Contains(lines, line => line.Contains("platform:Linux"));
    }
}
