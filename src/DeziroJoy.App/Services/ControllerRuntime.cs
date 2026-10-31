using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;

namespace DeziroJoy.App.Services;

/// <summary>
/// Per-device runtime: descriptor, active profile, engine and command dispatcher.
/// </summary>
public sealed class ControllerRuntime
{
    private readonly IOutputSink _sink;

    public ControllerRuntime(GamepadDescriptor descriptor, GamepadProfile profile, IOutputSink sink)
    {
        Descriptor = descriptor;
        _sink = sink;
        Profile = profile;
        Engine = new MappingEngine(profile);
        Dispatcher = new CommandDispatcher(sink);
    }

    public GamepadDescriptor Descriptor { get; }

    public GamepadProfile Profile { get; private set; }

    public MappingEngine Engine { get; private set; }

    public CommandDispatcher Dispatcher { get; private set; }

    /// <summary>Swaps in an edited profile, releasing anything currently held.</summary>
    public void Reload(GamepadProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Dispatcher.ReleaseAll();
        Engine.Reset();
        Profile = profile;
        Engine = new MappingEngine(profile);
        Dispatcher = new CommandDispatcher(_sink);
    }
}
