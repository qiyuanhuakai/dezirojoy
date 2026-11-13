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
    private readonly OutputOwnership _ownership;

    public ControllerRuntime(
        GamepadDescriptor descriptor,
        GamepadProfile profile,
        IOutputSink sink,
        OutputOwnership? ownership = null)
    {
        Descriptor = descriptor;
        _sink = sink;
        // One table per session, shared with every other device: two pads outputting the
        // same key must not lift it from one another.
        _ownership = ownership ?? new OutputOwnership();
        Profile = profile;
        Engine = new MappingEngine(profile);
        Dispatcher = new CommandDispatcher(sink, _ownership);
    }

    public GamepadDescriptor Descriptor { get; }

    public GamepadProfile Profile { get; private set; }

    public MappingEngine Engine { get; private set; }

    public CommandDispatcher Dispatcher { get; private set; }

    /// <summary>The temporary layer the engine is running, or 0 for the base layer alone.</summary>
    public int ActiveLayerId => Engine.ActiveLayerId;

    /// <summary>The mappings the engine currently has in effect, layer included.</summary>
    public IReadOnlyList<MappingEntry> EffectiveMappings => Engine.EffectiveMappings;

    /// <summary>
    /// The layer state the daemon's snapshot reports for this device: the active layer and
    /// every mapping it puts in effect.
    /// </summary>
    public ActiveLayerSnapshot LayerSnapshot => new(
        Descriptor.DeviceId,
        Engine.ActiveLayerId,
        [.. Engine.EffectiveMappings.Select(mapping => new EffectiveMappingSnapshot(mapping.Id, mapping.Layer))]);

    /// <summary>
    /// Swaps in an edited profile, releasing anything currently held. The new engine adopts
    /// <paramref name="baseline"/> - the device's last polled snapshot - when the host has one,
    /// so a key the user is still holding does not fabricate a fresh press edge and is not
    /// silently re-pressed after the release: it fires again on the next real press edge.
    /// </summary>
    public void Reload(GamepadProfile profile, IGamepadState? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Dispatcher.ReleaseAll();
        Engine.Reset();
        Profile = profile;
        Engine = new MappingEngine(profile);
        if (baseline is not null)
        {
            Engine.AdoptBaseline(baseline);
        }

        // The new dispatcher shares the session's ownership table, so the reload cannot
        // orphan a hold another device still owns.
        Dispatcher = new CommandDispatcher(_sink, _ownership);
    }
}
