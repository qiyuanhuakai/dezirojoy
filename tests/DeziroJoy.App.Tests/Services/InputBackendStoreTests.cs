using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The persisted input-backend selection. A missing file is the platform default every
/// existing install starts from, and a damaged one costs the selection and nothing else -
/// the same rules the foreground rule table follows, pinned here so a build that never
/// writes the file keeps reading through XInput or evdev exactly as it did.
/// </summary>
public class InputBackendStoreTests
{
    [Fact]
    public void A_Missing_File_Is_The_Platform_Default_And_Says_Nothing()
    {
        var store = new InputBackendStore(Path.Combine(Path.GetTempPath(), $"dezirojoy-backend-{Guid.NewGuid():N}.json"));

        Assert.Equal(InputBackend.Platform, store.Load());
        Assert.Null(store.LastError);
    }

    [Fact]
    public void A_Round_Trip_Keeps_The_Selection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dezirojoy-backend-{Guid.NewGuid():N}.json");
        try
        {
            new InputBackendStore(path).Save(InputBackend.Sdl3);

            Assert.Equal(InputBackend.Sdl3, new InputBackendStore(path).Load());
            Assert.Contains("sdl3", File.ReadAllText(path));

            new InputBackendStore(path).Save(InputBackend.Platform);

            Assert.Equal(InputBackend.Platform, new InputBackendStore(path).Load());
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void A_Damaged_File_Costs_The_Selection_And_Nothing_Else()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dezirojoy-backend-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "this is not json");

            var store = new InputBackendStore(path);

            Assert.Equal(InputBackend.Platform, store.Load());
            Assert.Contains("无法解析", store.LastError);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void An_Unknown_Backend_Name_Falls_Back_With_The_Reason()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dezirojoy-backend-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"backend":"directinput"}""");

            var store = new InputBackendStore(path);

            Assert.Equal(InputBackend.Platform, store.Load());
            Assert.Contains("无法识别", store.LastError);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Saving_Leaves_No_Temporary_Files_Behind()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dezirojoy-backend-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, InputBackendStore.FileName);
        try
        {
            Directory.CreateDirectory(directory);

            new InputBackendStore(path).Save(InputBackend.Sdl3);

            // The profiles' atomic writer: the finished file is the only thing left, so a
            // daemon reading the selection mid-save sees the old body or the new one.
            Assert.Equal(
                [InputBackendStore.FileName],
                Directory.GetFiles(directory).Select(Path.GetFileName));
            Assert.Equal(InputBackend.Sdl3, new InputBackendStore(path).Load());
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void The_In_Memory_Store_Round_Trips_Without_Touching_Disk()
    {
        var store = new InMemoryInputBackendStore();

        Assert.Equal(InputBackend.Platform, store.Load());

        store.Save(InputBackend.Sdl3);

        Assert.Equal(InputBackend.Sdl3, store.Load());
        Assert.Null(store.LastError);
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
