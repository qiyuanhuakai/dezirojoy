using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Services;

/// <summary>
/// The built-in scheme templates: complete, mutually-consistent schemes a user imports once to
/// get a working behavior instead of assembling one mapping at a time. The first is the Windows
/// pair - a desktop preset and a gaming preset that share one device-global special-key set, so
/// flipping between them never changes how the special keys behave.
/// </summary>
public static class BuiltInSchemeTemplates
{
    /// <summary>Every template this build ships, in the order the UI offers them.</summary>
    public static IReadOnlyList<SchemeBundle> All => [Windows];

    /// <summary>
    /// The Windows pair: slot 1 「桌面办公」 for browser and editor work, slot 2 「游戏竞技」
    /// for shooters and action games. Both carry the same three global special keys - a
    /// precision key on the left-stick click, a leader on Back that flips between the two
    /// presets, and a leader on RB that switches the temporary layer - so the special keys
    /// behave identically whichever preset is active.
    /// </summary>
    public static SchemeBundle Windows { get; } = CreateWindows();

    private static SchemeBundle CreateWindows()
    {
        var desktop = CreateDesktopPreset();
        var gaming = CreateGamingPreset();

        return new SchemeBundle
        {
            Name = "Windows 桌面与游戏方案",
            AppVersion = SchemeBundleFactory.CurrentAppVersion(),
            Notes =
                "Windows 上的两套完整预设。槽位 1「桌面办公」面向浏览器与编辑器：左摇杆滚动页面，" +
                "右摇杆移动鼠标，十字键是方向键，X/Y 是复制/粘贴，A 取消、B 确认；按住 RB 进入临时层，" +
                "临时层里 X/Y 变为剪切/撤销、A/B 是关闭窗口/显示桌面、十字键上下是翻页。" +
                "槽位 2「游戏竞技」面向射击与动作游戏：右摇杆以更高速度瞄准，左摇杆推四方向相当于 " +
                "WASD，左右扳机是鼠标左右键，十字键选物品栏；按住 RB 的临时层里 A/B/X/Y 是地图、" +
                "背包、记分板和聊天发送。" +
                "两套预设共享同一组全局特殊键：左摇杆按下是精准键（按一下启用、再按一下取消，瞄准更稳），" +
                "Back 键按下在两套预设之间切换，RB 按住进入临时层。" +
                "导入后会整体替换当前设备的全部预设，可在导入确认后撤销一次。",
            Presets =
            [
                new SchemePreset
                {
                    Slot = 1,
                    Profile = desktop,
                    ActionNames = SchemeBundleFactory.ActionNamesOf(desktop),
                },
                new SchemePreset
                {
                    Slot = 2,
                    Profile = gaming,
                    ActionNames = SchemeBundleFactory.ActionNamesOf(gaming),
                },
            ],
            SpecialKeys =
            [
                PrecisionKey(),
                LeaderKey(),
                LayerLeaderKey(),
            ],
            Layers =
            [
                new SchemeLayer
                {
                    LayerId = 1,
                    PresetSlot = 1,
                    Actions = SchemeBundleFactory.ActionNamesOf(
                        desktop.Mappings.Where(mapping => mapping.Layer == 1)),
                },
                new SchemeLayer
                {
                    LayerId = 1,
                    PresetSlot = 2,
                    Actions = SchemeBundleFactory.ActionNamesOf(
                        gaming.Mappings.Where(mapping => mapping.Layer == 1)),
                },
            ],
        };
    }

    /// <summary>
    /// 槽位 1 「桌面办公」: the everyday-desktop shape - wheel on the left stick, mouse on the
    /// right, the face buttons as the keys every editor agrees on, and a temporary layer for
    /// window management and paging.
    /// </summary>
    private static GamepadProfile CreateDesktopPreset() => new()
    {
        Name = "Windows 桌面办公",
        Layout = GamepadLayout.Xbox,
        PresetSlot = 1,
        Mappings =
        [
            // 左摇杆 -> 滚轮，右摇杆 -> 鼠标
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left),
                Target = new WheelScrollTarget(new ScrollStickSettings()),
            },
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Right),
                Target = new MouseMoveTarget(new MouseStickSettings()),
            },
            // 十字键 -> 方向键
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadUp),
                Target = new KeyPressTarget(GamepadKey.ArrowUp),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadDown),
                Target = new KeyPressTarget(GamepadKey.ArrowDown),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadLeft),
                Target = new KeyPressTarget(GamepadKey.ArrowLeft),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadRight),
                Target = new KeyPressTarget(GamepadKey.ArrowRight),
            },
            // 功能键：A 取消、B 确认、X 复制、Y 粘贴、Start 任务管理器
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.A),
                Target = new KeyPressTarget(GamepadKey.Escape),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.B),
                Target = new KeyPressTarget(GamepadKey.Enter),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.X),
                Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Y),
                Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.V),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Start),
                Target = new KeyComboTarget(KeyModifiers.Ctrl | KeyModifiers.Shift, GamepadKey.Escape),
            },
            // 全局特殊键（两套预设共用）
            PrecisionMapping(),
            LeaderMapping(),
            LayerLeaderMapping(),
            // 临时层（按住 RB）：窗口管理与翻页
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.A),
                Target = new KeyComboTarget(KeyModifiers.Alt, GamepadKey.F4),
                Layer = 1,
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.B),
                Target = new KeyComboTarget(KeyModifiers.Win, GamepadKey.D),
                Layer = 1,
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.X),
                Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.X),
                Layer = 1,
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Y),
                Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.Z),
                Layer = 1,
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadUp),
                Target = new KeyPressTarget(GamepadKey.PageUp),
                Layer = 1,
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadDown),
                Target = new KeyPressTarget(GamepadKey.PageDown),
                Layer = 1,
            },
        ],
    };

    /// <summary>
    /// 槽位 2 「游戏竞技」: the shooter shape - WASD on the left stick's four directions, a
    /// faster right-stick aim, the triggers as the mouse buttons, the D-pad as the item slots,
    /// and a temporary layer for the overlay keys every game agrees on.
    /// </summary>
    private static GamepadProfile CreateGamingPreset() => new()
    {
        Name = "Windows 游戏竞技",
        Layout = GamepadLayout.Xbox,
        PresetSlot = 2,
        Mappings =
        [
            // 右摇杆 -> 鼠标（更高的瞄准速度、更小的死区）
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Right),
                Target = new MouseMoveTarget(new MouseStickSettings
                {
                    MaxSpeed = 2600f,
                    DeadZone = 0.08f,
                    ResponseTime = 0.04f,
                }),
            },
            // 左摇杆四方向 -> WASD
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Up },
                Target = new KeyPressTarget(GamepadKey.W),
            },
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Down },
                Target = new KeyPressTarget(GamepadKey.S),
            },
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Left },
                Target = new KeyPressTarget(GamepadKey.A),
            },
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Right },
                Target = new KeyPressTarget(GamepadKey.D),
            },
            // 扳机 -> 鼠标左右键
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.LeftTrigger),
                Target = new MouseButtonTarget(MouseButton.Left),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.RightTrigger),
                Target = new MouseButtonTarget(MouseButton.Right),
            },
            // 十字键 -> 物品栏 1-4
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadUp),
                Target = new KeyPressTarget(GamepadKey.D1),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadDown),
                Target = new KeyPressTarget(GamepadKey.D2),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadLeft),
                Target = new KeyPressTarget(GamepadKey.D3),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.DPadRight),
                Target = new KeyPressTarget(GamepadKey.D4),
            },
            // 动作键：A 跳跃、B 交互、X 换弹、Y 使用、LB 手雷、Start 菜单
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.A),
                Target = new KeyPressTarget(GamepadKey.Space),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.B),
                Target = new KeyPressTarget(GamepadKey.E),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.X),
                Target = new KeyPressTarget(GamepadKey.R),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Y),
                Target = new KeyPressTarget(GamepadKey.F),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.LeftBumper),
                Target = new KeyPressTarget(GamepadKey.G),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Start),
                Target = new KeyPressTarget(GamepadKey.Escape),
            },
            // 全局特殊键（两套预设共用）
            PrecisionMapping(),
            LeaderMapping(),
            LayerLeaderMapping(),
            // 临时层（按住 RB）：地图、背包、记分板、聊天
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.A),
                Target = new KeyPressTarget(GamepadKey.M),
                Layer = 1,
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.B),
                Target = new KeyPressTarget(GamepadKey.I),
                Layer = 1,
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.X),
                Target = new KeyPressTarget(GamepadKey.Tab),
                Layer = 1,
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Y),
                Target = new KeyPressTarget(GamepadKey.Enter),
                Layer = 1,
            },
        ],
    };

    /// <summary>精准键：左摇杆按下，按一下启用、再按一下取消。</summary>
    private static PresetStore.DeviceSpecialKey PrecisionKey() => new(
        GamepadButton.LeftStick,
        Code: 0,
        SpecialKeyKind.Precision,
        PresetStore.SpecialKeyPosition.Target,
        new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Toggle),
        Enabled: true);

    /// <summary>Leader 键：Back 按下，在两套预设之间切换。</summary>
    private static PresetStore.DeviceSpecialKey LeaderKey() => new(
        GamepadButton.Back,
        Code: 0,
        SpecialKeyKind.Leader,
        PresetStore.SpecialKeyPosition.Target,
        new SpecialKeyTarget(SpecialKeyKind.Leader, SwitchMode: LeaderSwitchMode.Press, SlotA: 1, SlotB: 2),
        Enabled: true);

    /// <summary>临时层 Leader 键：按住 RB 进入临时层。</summary>
    private static PresetStore.DeviceSpecialKey LayerLeaderKey() => new(
        GamepadButton.RightBumper,
        Code: 0,
        SpecialKeyKind.Leader,
        PresetStore.SpecialKeyPosition.Target,
        LayerLeaderTarget(),
        Enabled: true);

    /// <summary>
    /// The precision key as the mapping both presets carry. The special keys are
    /// device-global, so every slot of the device holds the same three; the bundle states the
    /// subset once in its special-key section and the presets carry them as mappings.
    /// </summary>
    private static MappingEntry PrecisionMapping() => new()
    {
        Source = new ButtonBinding(GamepadButton.LeftStick),
        Target = new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Toggle),
        Enabled = true,
    };

    private static MappingEntry LeaderMapping() => new()
    {
        Source = new ButtonBinding(GamepadButton.Back),
        Target = new SpecialKeyTarget(
            SpecialKeyKind.Leader,
            SwitchMode: LeaderSwitchMode.Press,
            SlotA: 1,
            SlotB: 2),
        Enabled = true,
    };

    /// <summary>
    /// The leader key that switches the temporary layer, as the mapping both presets carry:
    /// 按住 RB enters the one temporary layer, exactly as the layer key did before the merge.
    /// </summary>
    private static MappingEntry LayerLeaderMapping() => new()
    {
        Source = new ButtonBinding(GamepadButton.RightBumper),
        Target = LayerLeaderTarget(),
        Enabled = true,
    };

    /// <summary>A leader key that switches the one temporary layer while it is held.</summary>
    private static SpecialKeyTarget LayerLeaderTarget() => new(
        SpecialKeyKind.Leader,
        SwitchMode: LeaderSwitchMode.Hold,
        Action: LeaderAction.Layer);
}
