using System.Diagnostics;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using DeziroJoy.Core.Profiles;

namespace DeziroJoy.App.Services;

/// <summary>
/// Deterministic provider-layer gamepad for the opt-in performance baseline. It is not a
/// virtual HID/XInput driver and makes no claim about Bluetooth or OS input delivery latency.
/// The production engine, wall-clock loop and real output sink remain in the measured path.
/// </summary>
public sealed class VirtualGamepadProvider : IGamepadProvider
{
    public const string ModeVariable = "DEZIROJOY_VIRTUAL_PAD";
    public const string OverlayVariable = "DEZIROJOY_PERF_OVERLAY";

    private readonly string _mode;
    private readonly GamepadDescriptor[] _descriptors;
    private readonly GamepadDescriptor[] _connected;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly bool _overlay;

    private VirtualGamepadProvider(string mode, bool overlay)
    {
        _mode = mode;
        _overlay = overlay;
        var count = mode == "multi" ? 4 : mode == "none" ? 0 : 1;
        _descriptors = Enumerable.Range(0, count)
            .Select(index => new GamepadDescriptor($"virtual-perf-{index + 1}", $"Xbox Virtual Performance Pad #{index + 1}"))
            .ToArray();
        _connected = (GamepadDescriptor[])_descriptors.Clone();
    }

    public static bool TryCreate(out IGamepadProvider provider)
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode))
        {
            provider = null!;
            return false;
        }

        if (mode is not ("none" or "idle" or "mouse" or "macro" or "multi"))
        {
            throw new InvalidOperationException(
                $"{ModeVariable} 必须为 none、idle、mouse、macro 或 multi，实际为 {mode}。");
        }

        // Seeding virtual profiles is only valid inside the baseline's isolated environment.
        // Requiring both diagnostics variables prevents a stray ModeVariable from rewriting the
        // user's everyday profiles; the runner additionally redirects APPDATA/LOCALAPPDATA.
        if (Environment.GetEnvironmentVariable("DEZIROJOY_PERF_METRICS") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEZIROJOY_PERF_DIR")))
        {
            throw new InvalidOperationException(
                $"{ModeVariable} 只能在性能基线隔离环境中使用：同时设置 DEZIROJOY_PERF_METRICS=1 与 DEZIROJOY_PERF_DIR。");
        }

        provider = new VirtualGamepadProvider(
            mode,
            Environment.GetEnvironmentVariable(OverlayVariable) == "1");
        return true;
    }

    public IReadOnlyList<GamepadDescriptor> GetConnected() => _connected;

    public IGamepadState Poll(string deviceId)
    {
        var index = Array.FindIndex(_descriptors, descriptor => descriptor.DeviceId == deviceId);
        if (index < 0)
        {
            return null!;
        }

        var elapsed = (Stopwatch.GetTimestamp() - _started) / (double)Stopwatch.Frequency;
        var buttons = GamepadButtonMask.None;
        if (_mode == "macro")
        {
            buttons |= GamepadButton.A.ToMask();
        }

        if (_overlay)
        {
            buttons |= GamepadButton.RightBumper.ToMask();
        }

        var rightX = 0f;
        if (_mode is "mouse" or "multi")
        {
            // Square-wave往返 keeps continuous SendInput traffic without walking the pointer
            // off the desktop. Multi-device phases prevent all four pads moving in lockstep.
            var phase = (elapsed + index * 0.25) % 2.0;
            rightX = phase < 1.0 ? 0.75f : -0.75f;
        }

        return GamepadState.Create(buttons, 0, 0, rightX, 0);
    }

    /// <summary>Seeds isolated profiles; ordinary user configuration is never read or changed.</summary>
    public void SeedProfiles(IProfileStore store)
    {
        var presets = new PresetStore(store);
        foreach (var descriptor in _descriptors)
        {
            var profile = DefaultProfileLibrary.CreateDefault(descriptor) with
            {
                Name = $"性能基线 · {_mode} · {descriptor.DeviceId}",
            };
            var mappings = profile.Mappings.ToList();

            if (_mode == "macro")
            {
                mappings.RemoveAll(mapping => mapping.Source is ButtonBinding { Button: GamepadButton.A });
                mappings.Add(new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.A)
                    {
                        Gesture = new ButtonGesture
                        {
                            LongPressThreshold = 0.25f,
                            LongPressAction = new ComplexShortcutTarget
                            {
                                Script = "hold F23\nevery 50ms tap F24",
                            },
                        },
                    },
                    Target = new NoneTarget(),
                });
            }

            if (_overlay)
            {
                mappings.Add(new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.RightBumper),
                    Target = new SpecialKeyTarget(
                        SpecialKeyKind.Leader,
                        SwitchMode: LeaderSwitchMode.Hold,
                        Action: LeaderAction.Layer),
                });
                mappings.Add(new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.X),
                    Target = new KeyPressTarget(GamepadKey.F24),
                    Layer = 1,
                });
            }

            for (var slot = PresetStore.MinSlot; slot <= PresetStore.DefaultSlotCount; slot++)
            {
                presets.SaveSlot(descriptor.DeviceId, slot, profile with
                {
                    Mappings = mappings,
                    PresetSlot = slot,
                });
            }
        }
    }

    public void Dispose()
    {
    }
}
