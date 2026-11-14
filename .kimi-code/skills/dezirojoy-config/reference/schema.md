# DeziroJoy config — full field & enum reference

Companion to `SKILL.md`. All JSON is indented, enums as names, polymorphic objects carry `$type`.
Default-valued fields are omitted on write and may be omitted when authoring.

## GamepadProfile (a profile / preset)

| Field | Type | Default | Notes |
|---|---|---|---|
| `SchemaVersion` | int | 3 | **must be 3** |
| `Name` | string | — | required |
| `DeviceId` | string? | null | null = portable/global; else binds to a device |
| `Layout` | enum | `Generic` | `Xbox` `DualShock` `Generic` |
| `PresetSlot` | int | 1 | the slot this profile occupies |
| `Mappings` | array | `[]` | list of MappingEntry |

## MappingEntry

| Field | Type | Default | Notes |
|---|---|---|---|
| `Id` | GUID | new | generate a GUID |
| `Source` | InputBinding | — | required, `$type` = `button`/`stick` |
| `Target` | MappingTarget | — | required, `$type` per target table |
| `Enabled` | bool | true | |
| `Layer` | int | 0 | 0 = base, 1 = the temporary layer; omit when 0 |

## InputBinding

### ButtonBinding (`$type: "button"`)
| Field | Type | Default | Notes |
|---|---|---|---|
| `Button` | GamepadButton | — | required |
| `Code` | int | 0 | only for `Extra`; omit otherwise |
| `Direction` | InputDirection? | null | required for button→continuous target |
| `Gesture` | ButtonGesture? | null | optional |

### StickBinding (`$type: "stick"`)
| Field | Type | Default | Notes |
|---|---|---|---|
| `Stick` | GamepadStick | — | `Left` `Right` |
| `Direction` | InputDirection? | null | required for stick→digital target |

## Enums

- **GamepadButton**: `A B X Y LeftBumper RightBumper LeftTrigger RightTrigger Back Start Guide
  LeftStick RightStick DPadUp DPadDown DPadLeft DPadRight Extra`
- **GamepadStick**: `Left Right`
- **InputDirection**: `Up Down Left Right`
- **MouseButton**: `Left Right Middle XButton1 XButton2`
- **KeyModifiers** ([Flags]): `None Ctrl Shift Alt Win`; combine as `"Ctrl, Shift"`
- **ResponseCurve**: `Linear Precise Aggressive Responsive`
- **PrecisionMode**: `Hold Toggle`
- **LeaderSwitchMode**: `Press Hold`
- **SpecialKeyKind**: `Precision Leader Keyboard` (`Layer` is deprecated → migrates to Leader+Action=Layer)
- **LeaderAction**: `Preset Layer`
- **GamepadLayout**: `Xbox DualShock Generic`
- **ForegroundAction**: `Profile NativeGamepad`

### GamepadKey (complete)
Letters `A B C D E F G H I J K L M N O P Q R S T U V W X Y Z`;
digits `D0 D1 D2 D3 D4 D5 D6 D7 D8 D9`;
`F1`…`F24`; `Numpad0`…`Numpad9`;
`Escape Enter Space Tab Backspace Delete Insert Home End PageUp PageDown`;
`ArrowUp ArrowDown ArrowLeft ArrowRight`;
`LeftCtrl LeftShift LeftAlt LeftWin`;
`Minus Equal Comma Period Slash Semicolon Apostrophe BracketLeft BracketRight Backslash Grave`;
media/browser/system: `VolumeUp VolumeDown VolumeMute MediaPlayPause MediaStop MediaPrevTrack
MediaNextTrack BrowserBack BrowserForward BrowserRefresh BrowserStop BrowserHome BrowserSearch
BrowserFavorites LaunchMail LaunchApp1 LaunchApp2 Sleep PrintScreen`.

## MappingTarget types

| `$type` | Record fields | Behavior |
|---|---|---|
| `key` | `Key: GamepadKey` | down on press, up on release |
| `combo` | `Modifiers: KeyModifiers`, `Key: GamepadKey` | tap chord on press edge |
| `mouseButton` | `Button: MouseButton` | down on press, up on release |
| `mouseMove` | `Settings: MouseStickSettings` | stick → pointer |
| `wheel` | `Settings: ScrollStickSettings` | stick → scroll |
| `specialKey` | see below | precision / Leader / screen keyboard |
| `text` | `Text: string` (≤256) | type string on press edge (tap) |
| `complexShortcut` | `Script: string`, `Trigger`, `Execution`, `Repeat` | DSL program |
| `none` | — | do nothing (无功能) |

### SpecialKeyTarget (`$type: "specialKey"`)
| Field | Type | Default | Notes |
|---|---|---|---|
| `Kind` | SpecialKeyKind | — | `Precision`, `Leader` or `Keyboard` |
| `PrecisionMode` | PrecisionMode | `Toggle` | Precision only: `Hold`/`Toggle` |
| `SwitchMode` | LeaderSwitchMode | `Press` | Leader only: `Press`/`Hold` |
| `SlotA` | int | 1 | Leader+Preset: primary slot |
| `SlotB` | int | 2 | Leader+Preset: alternate slot |
| `Action` | LeaderAction | `Preset` | Leader only: `Preset` or `Layer`; omit when Preset |

Rules: Precision must be the mapping's own single-click target. Leader/Layer on base layer.
Layer leader: Press or Hold; single temporary layer. A layer mapping must not sit on its own
leader's input. Special keys are device-global (sync across presets).
Keyboard: toggles the floating on-screen keyboard (render process only; tray-only shows no
window). Every press edge toggles, so SwitchMode is meaningless for it; the engine emits a
host-side command that never reaches the output sink. While the keyboard is open the pad drives
it and mapping injection is suspended, so the key that summoned it is answered by the window
(B / 完成 / the key itself), not by a second engine press.

## ButtonGesture

| Field | Type | Default | Notes |
|---|---|---|---|
| `DoubleClickWindow` | float(s) | 0 | 0 disables; default when enabled 0.25 |
| `LongPressThreshold` | float(s) | 0 | 0 disables; default when enabled 0.50 |
| `LongPressRepeatInterval` | float(s) | 0 | long-press auto-repeat; 0 = once |
| `ClickRepeatInterval` | float(s) | 0 | click auto-repeat; 0 = once; **excludes long-press** |
| `DoubleClickAction` | Target? | null | null = re-fire mapping target |
| `LongPressAction` | Target? | null | null = re-fire mapping target |

## MouseStickSettings (`mouseMove.Settings`)

| Field | Type | Default | Notes |
|---|---|---|---|
| `DeadZone` | float | 0.15 | radial fraction 0..0.9 |
| `MaxSpeed` | float | 1600 | px/s at full deflection |
| `Curve` | ResponseCurve | `Precise` | |
| `CurveStrength` | float | 1 | **[0,1]**; blend linear↔curve |
| `ResponseTime` | float(s) | 0.08 | smoothing; 0 = instant |
| `PrecisionScale` | float | 0.35 | 0..1 multiplier while precision engaged |
| `PrecisionButton` | GamepadButton | `LeftStick` | legacy/migration-only |
| `PrecisionMode` | PrecisionMode | `Hold` | legacy/migration-only |
| `ScaleX` | float | 1 | horizontal gain |
| `ScaleY` | float | 1 | vertical gain |
| `InvertY` | bool | true | stick up = cursor up |

## ScrollStickSettings (`wheel.Settings`)

| Field | Type | Default | Notes |
|---|---|---|---|
| `DeadZone` | float | 0.2 | radial fraction |
| `MaxTicksPerSecond` | float | 12 | notches/s at full deflection |
| `Curve` | ResponseCurve | `Precise` | |
| `CurveStrength` | float | 1 | **[0,1]** |
| `ResponseTime` | float(s) | 0.05 | smoothing |
| `InvertY` | bool | false | |
| `EnableHorizontal` | bool | true | horizontal deflection → horizontal scroll |

## ComplexShortcutTarget (`$type: "complexShortcut"`)

| Field | Type | Default | Notes |
|---|---|---|---|
| `Script` | string | — | required; DSL source of truth |
| `Trigger` | ActionTrigger | `LongPress` | omit at default |
| `Execution` | ActionExecution | `WhileHeld` | omit at default |
| `Repeat` | ActionRepeat | `Restart` | omit at default |

- **ActionTrigger**: `LongPress Press DoubleClick Release`
- **ActionExecution**: `WhileHeld Once Toggle`
- **ActionRepeat**: `Restart Ignore Queue`

DSL header words (case-insensitive, Chinese accepted):
- `trigger`: `longpress`/`long`/`长按`, `press`/`click`/`单击`/`按下`, `doubleclick`/`double`/`双击`,
  `release`/`松开`
- `run`: `whileheld`/`held`/`按住期间`/`按住`, `once`/`一次`, `toggle`/`切换`/`再按切换`
- `repeat`: `restart`/`重新开始`/`重来`, `ignore`/`忽略`, `queue`/`排队`

Steps: `tap <Key>`, `hold <Key>`, `release <Key>`, `wait <dur>`, `every <dur> tap <Key>`,
`click <MouseButton>`, `text <string>`. Duration `1s`/`0.5s`/`500ms` (bounded ~0.01s..60s).
`#` comment. Headers before steps. Constraints: `release` trigger ⇒ `run once`; `doubleclick`
trigger ⇒ not `run whileheld`; `queue` ⇒ not `whileheld`. A program is a gesture action (usually
`LongPressAction`), never a stick target; it cannot coexist with click auto-repeat or a
long-press repeat interval.

## SchemeBundle

| Field | Type | Default | Notes |
|---|---|---|---|
| `SchemaVersion` | int | 1 | must be 1 |
| `Name` | string | — | required |
| `AppVersion` | string? | null | version that wrote it |
| `Notes` | string? | null | usage notes |
| `Presets` | array | `[]` | SchemePreset; unique slots ≥1 |
| `SpecialKeys` | array? | null | device-global special keys; null = take presets' subset |
| `Layers` | array | `[]` | SchemeLayer summaries |

- **SchemePreset**: `Slot:int`, `Profile:GamepadProfile`, `ActionNames:[{Source,Action}]`
- **SchemeLayer**: `LayerId:int`, `PresetSlot:int`, `Actions:[{Source,Action}]`
- A body with no `Presets` is re-read as a plain profile → one-preset scheme (slot 1).

## ForegroundSettings

| Field | Type | Default | Notes |
|---|---|---|---|
| `Rules` | array | `[]` | ForegroundRule |
| `DefaultSlot` | int | 1 | fallback profile slot |
| `Enabled` | bool | true | whole feature on/off |

### ForegroundRule
| Field | Type | Default | Notes |
|---|---|---|---|
| `Id` | GUID | new | |
| `ProcessName` | string | — | required; case/.exe-insensitive; `*` = any |
| `Priority` | int | 0 | higher wins; tie = list order |
| `Action` | ForegroundAction | `Profile` | `Profile` or `NativeGamepad` |
| `Slot` | int | 1 | for `Profile` |
| `Enabled` | bool | true | |
| `FullscreenOnly` | bool | false | auxiliary; still matches if fullscreen undetectable |