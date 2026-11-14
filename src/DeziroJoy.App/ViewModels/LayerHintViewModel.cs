using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeziroJoy.App.Ipc;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>One line of the layer hint overlay: the gamepad element that triggers it and what the
/// layer makes it do - X：复制 rather than X：Ctrl + C. The key is the element's short name,
/// the action its readable name, so a player holding a layer leader reads what each button
/// DOES without decoding a key combo mid-fight.</summary>
public sealed record LayerHintRow(string Key, string Action);

/// <summary>
/// Where the layer hint overlay sits on screen. <see cref="Fixed"/> parks it at a chosen
/// <see cref="LayerHintAnchor"/>; <see cref="FollowMouse"/> puts it beside the pointer the
/// moment a layer leader is held, so the hint appears where the user is already looking
/// instead of at a fixed corner.
/// </summary>
public enum LayerHintPositionMode
{
    /// <summary>Park the hint at the chosen screen edge or corner.</summary>
    Fixed,

    /// <summary>Place the hint beside the current pointer when the layer appears.</summary>
    FollowMouse,
}

/// <summary>
/// A screen edge or corner the fixed hint parks at. The three bottom anchors keep the hint
/// clear of the top of the screen where a game's own HUD usually lives; the side anchors park
/// it against a vertical edge for a wide target that uses the bottom strip.
/// </summary>
public enum LayerHintAnchor
{
    BottomCenter,
    BottomLeft,
    BottomRight,
    TopCenter,
    TopLeft,
    TopRight,
    MiddleLeft,
    MiddleRight,
}

/// <summary>The overlay's placement, read from the persisted preferences each time it moves.</summary>
public readonly record struct LayerHintPlacement(LayerHintPositionMode Mode, LayerHintAnchor Anchor);

/// <summary>
/// What the layer hint overlay shows while a temporary layer is active: the layer's own
/// mappings, named as readable actions. Built from the daemon's snapshot - the layer the
/// device is really running, with the mappings that layer puts in effect - resolved against
/// the profile the snapshot carries, so the hint never derives state from the profile body
/// alone. A device running its base mappings alone, a layer whose mappings have all left
/// the profile, or a lost daemon connection all leave the hint inactive and empty, which is
/// what hides the overlay.
/// </summary>
public partial class LayerHintViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    /// <summary>
    /// True while the hint is dismissed for this session: the overlay stays hidden even if a
    /// layer leader is held. The overlay is click-through, so the manual close lives outside it
    /// as the 显示临时层提示 preference; turning that off raises this flag, turning it back on
    /// clears it. It is deliberately not derived from the layer - releasing the leader clears
    /// <see cref="IsActive"/>, never this.
    /// </summary>
    [ObservableProperty]
    private bool _isSuppressed;

    [ObservableProperty]
    private string _layerTitle = string.Empty;

    /// <summary>The layer's own mappings as hint rows, in the order the engine runs them.</summary>
    public ObservableCollection<LayerHintRow> Rows { get; } = [];

    /// <summary>
    /// Rebuilds the hint from the profile in view and the latest daemon snapshot. Only the
    /// mappings the active layer itself contributes are listed - the base mappings it
    /// inherits are not the layer's doing - and a mapping that is disabled or no longer in
    /// the profile is skipped, because the hint must only ever promise what fires.
    /// </summary>
    public void Update(GamepadProfile? profile, StateSnapshot? snapshot)
    {
        var layer = snapshot?.Layers.FirstOrDefault(active => active.LayerId > 0);
        var rows = layer is not null && profile is not null
            ? RowsFor(profile, layer)
            : [];

        if (layer is null || rows.Count == 0)
        {
            IsActive = false;
            LayerTitle = string.Empty;
            Rows.Clear();
            return;
        }

        LayerTitle = $"临时层 {layer.LayerId}";
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        IsActive = true;
    }

    private static List<LayerHintRow> RowsFor(GamepadProfile profile, ActiveLayerSnapshot layer)
    {
        var rows = new List<LayerHintRow>();

        foreach (var effective in layer.Mappings)
        {
            // The effective list carries the inherited base mappings too; the layer's own
            // are exactly the ones that name it.
            if (effective.Layer != layer.LayerId)
            {
                continue;
            }

            var entry = profile.Mappings.FirstOrDefault(mapping => mapping.Id == effective.MappingId);
            if (entry is null || !entry.Enabled)
            {
                continue;
            }

            rows.Add(new LayerHintRow(ShortSource(entry.Source), MappingDescriptions.ActionName(entry.Target)));
        }

        return rows;
    }

    /// <summary>
    /// The compact name a hint row gives its gamepad element: a lone letter on the face
    /// buttons, the shoulder abbreviations, the stick by side. The full names the mapping
    /// rows use ("X 键") read as sentences in a one-glance overlay, so the overlay keeps its
    /// own short form.
    /// </summary>
    private static string ShortSource(InputBinding binding) => binding switch
    {
        ButtonBinding button => button.Button switch
        {
            GamepadButton.A => "A",
            GamepadButton.B => "B",
            GamepadButton.X => "X",
            GamepadButton.Y => "Y",
            GamepadButton.LeftBumper => "LB",
            GamepadButton.RightBumper => "RB",
            GamepadButton.LeftTrigger => "LT",
            GamepadButton.RightTrigger => "RT",
            GamepadButton.Back => "Back",
            GamepadButton.Start => "Start",
            GamepadButton.Guide => "Guide",
            GamepadButton.LeftStick => "左摇杆按下",
            GamepadButton.RightStick => "右摇杆按下",
            GamepadButton.DPadUp => "十字键 ↑",
            GamepadButton.DPadDown => "十字键 ↓",
            GamepadButton.DPadLeft => "十字键 ←",
            GamepadButton.DPadRight => "十字键 →",
            GamepadButton.Extra => button.Code > 0 ? $"按键 {button.Code}" : "扩展按键",
            _ => button.Button.ToString(),
        },
        StickBinding stick => stick.Stick == GamepadStick.Left ? "左摇杆" : "右摇杆",
        _ => binding.ToString() ?? string.Empty,
    };
}
