using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Services;

/// <summary>Persistence of per-device profiles.</summary>
public interface IProfileStore
{
    GamepadProfile? Load(string deviceId);

    void Save(GamepadProfile profile);

    bool Delete(string deviceId);

    IReadOnlyList<GamepadProfile> LoadAll();
}
