---
name: dezirojoy-config
description: Author, edit, and validate DeziroJoy gamepad-mapping configuration files — profile JSON, scheme bundles, and foreground app rules. Use when asked to write, generate, modify, review, or explain a DeziroJoy mapping profile / 配置 / 方案 / preset, a button or stick mapping, a gesture (单击/双击/长按), a Leader or precision key, a temporary layer, sensitivity/curve settings, a complex-shortcut DSL script, or a foreground app rule.
whenToUse: When the user asks to create or modify DeziroJoy mapping configurations, profiles, schemes, presets, or foreground rules, or to explain/validate the config file format.
---

# DeziroJoy configuration authoring

DeziroJoy maps gamepad input to keyboard and mouse. You are authoring its configuration
files. This skill gives you the exact JSON schema, the rules the engine enforces, and the
pitfalls that make a hand-written file fail to load. **Read the whole skill before writing a
file, and validate against the rules at the end before handing it over.**

Authoritative sources in this repo (read them if a detail here is unclear):
- Model / schema: `src/DeziroJoy.Core/Mapping/*.cs`
- Engine validation (what is rejected): `src/DeziroJoy.Core/Engine/MappingEngine.cs`
- Serializer: `src/DeziroJoy.Core/Mapping/ProfileSerializer.cs`
- Scheme bundle: `src/DeziroJoy.App/Services/SchemeBundle.cs`
- Foreground rules: `src/DeziroJoy.Core/Mapping/ForegroundRule.cs`, `ForegroundSettings.cs`

Full field/enum reference: `${KIMI_SKILL_DIR}/reference/schema.md`
Complete worked examples: `${KIMI_SKILL_DIR}/reference/examples.md`

## The three file types

| File | What it is | Where it lives |
|---|---|---|
| **Profile** (`GamepadProfile`) | One preset: a device's mapping table for one slot | `%APPDATA%\DeziroJoy\profiles\` (Win) / `~/.config/DeziroJoy/profiles/` (Linux). Slot 1 file is `<deviceId>.json`; slot N is `presetN_<deviceId>.json` |
| **Scheme bundle** (`SchemeBundle`) | A whole preset set + shared special keys + layers + notes, in one importable file | Anywhere; imported via 设置 → 导入方案. This is the preferred share/export unit |
| **Foreground rules** (`ForegroundSettings`) | Per-app profile switching / native-gamepad pause | `%APPDATA%\DeziroJoy\foreground-rules.json` / `~/.config/DeziroJoy/foreground-rules.json` |

All three are **indented JSON, enums written as names** (not numbers). Polymorphic objects
(`Source`, `Target`) carry a `"$type"` discriminator. Fields at their default value are
omitted on write; you may omit them too.

**Prefer authoring a scheme bundle** when the user wants a complete, shareable setup; author a
single profile when they only want one preset. A plain profile file also imports as a
one-preset scheme, so either works with 导入方案.

## Workflow

1. **Clarify the target**: which device/layout (`Xbox` / `DualShock` / `Generic`), which preset
   slot, and what each control should do. If the user describes behavior ("right stick moves the
   mouse, A jumps"), translate it into mappings.
2. **Pick the source and target** for each mapping (see below). A source is a button or a stick;
   a target is what it emits.
3. **Add gestures / special keys / layers** only if asked. Keep it minimal — every gesture adds
   latency, and special keys have strict placement rules.
4. **Write the JSON**, omitting default-valued fields. Use the exact enum names and `$type`
   discriminators from the reference.
5. **Validate** against the checklist below. If the repo is available, the strongest check is to
   load it: a profile must deserialize (`ProfileSerializer`) and build (`new MappingEngine(profile)`);
   a scheme must pass `SchemeBundleCodec.Parse`. If you can run code, do that; otherwise walk the
   checklist carefully.
6. **Hand over** the file plus a one-line note on where to put it / how to import it.

## Anatomy of a profile

```json
{
  "SchemaVersion": 3,
  "Name": "我的配置",
  "DeviceId": null,
  "Layout": "Xbox",
  "PresetSlot": 1,
  "Mappings": [
    {
      "Id": "3f2b...guid...",
      "Source": { "$type": "button", "Button": "A" },
      "Target": { "$type": "key", "Key": "Space" },
      "Enabled": true
    }
  ]
}
```

- `SchemaVersion` **must be 3** (current). `Name` is required. `DeviceId` is `null` for a
  portable/global profile, or the device id to bind it. `PresetSlot` is the slot (1 = base).
- Each mapping: `Id` (a GUID; generate one), `Source`, `Target`, `Enabled` (default true),
  `Layer` (0 = base, 1 = the temporary layer; omit when 0).

### Sources (`Source`, discriminated by `$type`)

- **Button** `{ "$type": "button", "Button": "<GamepadButton>", "Direction": null, "Gesture": null }`
  - `Button`: `LeftBumper RightBumper LeftTrigger RightTrigger Back Start Guide LeftStick
    RightStick DPadUp DPadDown DPadLeft DPadRight A B X Y Extra`. (`A B X Y` are the face buttons.)
  - `Code`: only for `Extra` (raw platform code); omit otherwise.
  - `Direction` (`Up Down Left Right`): **required** when a button drives a *continuous* target
    (mouseMove / wheel); omit for digital targets.
  - `Gesture`: optional, see below.
- **Stick** `{ "$type": "stick", "Stick": "Left|Right", "Direction": null }`
  - `Direction` is **required** when a stick drives a *digital* target (key/combo/mouseButton/
    specialKey); omit for mouseMove/wheel (which consume both axes).

### Targets (`Target`, discriminated by `$type`)

| `$type` | Shape | Use |
|---|---|---|
| `key` | `{ "Key": "<GamepadKey>" }` | hold-to-key (down on press, up on release) |
| `combo` | `{ "Modifiers": "Ctrl", "Key": "C" }` | tap a chord; `Modifiers` is flags: `Ctrl Shift Alt Win`, combined `"Ctrl, Shift"` |
| `mouseButton` | `{ "Button": "Left|Right|Middle|XButton1|XButton2" }` | hold a mouse button |
| `mouseMove` | `{ "Settings": { ...MouseStickSettings } }` | stick → pointer |
| `wheel` | `{ "Settings": { ...ScrollStickSettings } }` | stick → scroll |
| `specialKey` | `{ "Kind": "Precision|Leader", ... }` | precision / Leader (see below) |
| `text` | `{ "Text": "..." }` | type a short string (≤256 chars) on press |
| `complexShortcut` | `{ "Script": "...", "Trigger": ..., "Execution": ..., "Repeat": ... }` | DSL program |
| `none` | `{}` | deliberately do nothing (无功能) |

`GamepadKey` names: letters `A`–`Z`, digits `D0`–`D9`, `F1`–`F24`, `Numpad0`–`Numpad9`,
`Escape Enter Space Tab Backspace Delete Insert Home End PageUp PageDown ArrowUp ArrowDown
ArrowLeft ArrowRight LeftCtrl LeftShift LeftAlt LeftWin`, punctuation (`Minus Equal Comma Period
Slash Semicolon Apostrophe BracketLeft BracketRight Backslash Grave`), and media/browser keys
(`VolumeUp VolumeDown VolumeMute MediaPlayPause MediaStop MediaPrevTrack MediaNextTrack
BrowserBack BrowserForward BrowserRefresh BrowserStop BrowserHome BrowserSearch BrowserFavorites
LaunchMail LaunchApp1 LaunchApp2 Sleep PrintScreen`). Full list: reference/schema.md.

### Gestures (`Source.Gesture` on a button)

All thresholds are seconds; `0` disables that gesture. Omit the whole `Gesture` for a
zero-latency plain click.

```json
{ "DoubleClickWindow": 0.25, "LongPressThreshold": 0.5,
  "LongPressRepeatInterval": 0, "ClickRepeatInterval": 0,
  "DoubleClickAction": null, "LongPressAction": null }
```

- `DoubleClickAction` / `LongPressAction` are targets; `null` means "re-fire the mapping's own
  target". A `complexShortcut` program is normally the `LongPressAction`.
- **Click auto-repeat (`ClickRepeatInterval`) and long-press are mutually exclusive** — if both
  are set, click-repeat wins and long-press is inert. Don't set both.

### Special keys (`specialKey` target)

```json
{ "$type": "specialKey", "Kind": "Leader", "SwitchMode": "Hold", "SlotA": 1, "SlotB": 2, "Action": "Preset" }
```

- `Kind: "Precision"` — slows mouse sticks while engaged. `PrecisionMode`: `Hold` (while pressed)
  or `Toggle` (press to latch). Precision keys **must be on the single-click position** (the
  mapping's own `Target`, not a gesture action).
- `Kind: "Leader"` — `Action: "Preset"` flips between `SlotA`/`SlotB`; `Action: "Layer"` enters
  the temporary layer. `SwitchMode`: `Press` (toggle) or `Hold` (while held). **A Layer leader may
  use Press or Hold; there is only ONE temporary layer (`Layer: 1`).**
- Special keys are **device-global**: they sync across all of a device's presets. Place them on
  the base layer (`Layer: 0`).

### Temporary layer

Put a mapping on the layer with `"Layer": 1`. A Leader with `Action: "Layer"` activates it. The
layer inherits every base mapping it does not override; identical continuous actions survive the
switch. **A layer mapping must not sit on its own leader's input**, and a layer leader must be on
the base layer.

### Sensitivity settings

`mouseMove.Settings` (`MouseStickSettings`) and `wheel.Settings` (`ScrollStickSettings`) — full
fields in reference/schema.md. Key points:
- `Curve`: `Linear Precise Aggressive Responsive`. **`CurveStrength` is limited to `[0,1]`**
  (values outside are clamped; >1 used to reverse direction). Default 1.
- `DeadZone` radial fraction (mouse default 0.15, wheel 0.2). `MaxSpeed` px/s (mouse), 
  `MaxTicksPerSecond` notches/s (wheel). `ResponseTime` smoothing seconds (0 = instant).
- Mouse: `PrecisionScale` (0..1), `ScaleX`/`ScaleY`, `InvertY` (default true). Wheel: `InvertY`
  (default false), `EnableHorizontal` (default true).
- Note: px/s is DPI/resolution-independent but **still scaled by OS pointer-speed settings** —
  don't promise identical on-screen speed everywhere.

### Complex shortcut DSL (`complexShortcut`)

`Script` is the source of truth; `Trigger`/`Execution`/`Repeat` are optional header lines.
Defaults: `Trigger=LongPress`, `Execution=WhileHeld`, `Repeat=Restart` (omit to keep them).

```
trigger press        # longpress | press | doubleclick | release  (长按/单击/双击/松开)
run once             # whileheld | once | toggle                 (按住期间/一次/切换)
repeat restart       # restart | ignore | queue                  (重新开始/忽略/排队)
hold Alt
every 1s tap Tab
```

Steps: `tap <Key>`, `hold <Key>`, `release <Key>`, `wait <dur>`, `every <dur> tap <Key>`,
`click <MouseButton>`, `text <string>`. Durations: `1s`, `0.5s`, `500ms`. `#` starts a comment.
Header lines must come before steps. A `release` trigger must use `run once`; a `doubleclick`
trigger cannot use `run whileheld`; `queue` cannot pair with `whileheld`.

## Scheme bundle

```json
{
  "SchemaVersion": 1,
  "Name": "桌面办公",
  "AppVersion": "0.0.2-beta.4-preview",
  "Notes": "用途与用法说明…",
  "Presets": [ { "Slot": 1, "Profile": { ...GamepadProfile... }, "ActionNames": [ {"Source":"A 键","Action":"空格"} ] } ],
  "SpecialKeys": null,
  "Layers": [ { "LayerId": 1, "PresetSlot": 1, "Actions": [ ... ] } ]
}
```

- `Presets` is required and non-empty for a real bundle; each `Slot` ≥ 1 and unique; the embedded
  `Profile` is exactly a profile file. `ActionNames`/`Layers` are human-readable summaries (the
  profiles are the authority). `SpecialKeys: null` means "take the subset the presets carry".
- A plain profile file (no `Presets`) imports as a one-preset scheme automatically.

## Foreground rules

```json
{
  "Enabled": true,
  "DefaultSlot": 1,
  "Rules": [
    { "ProcessName": "chrome", "Priority": 10, "Action": "Profile", "Slot": 2, "Enabled": true, "FullscreenOnly": false },
    { "ProcessName": "eldenring", "Action": "NativeGamepad", "Priority": 20 }
  ]
}
```

- `ProcessName` matches case-insensitively, `.exe` suffix ignored; `"*"` matches every program.
- `Action`: `Profile` (activate `Slot`) or `NativeGamepad` (pause keyboard/mouse mapping so the app
  reads the pad directly). Higher `Priority` wins; ties keep list order. `FullscreenOnly` is an
  auxiliary condition (on platforms that can't detect fullscreen the rule still matches).
- No match / desktop / `Enabled:false` → `DefaultSlot`.

## Validation checklist (walk this before handing over)

1. `SchemaVersion` = 3 (profile) / 1 (bundle). `Name` present. Every mapping has `Id`, `Source`, `Target`.
2. Every `Source`/`Target` has the correct `$type`; every enum value is an exact name from the reference.
3. Button → continuous target has `Direction`; stick → digital target has `Direction`. Native
   pairings (button→digital, stick→mouseMove/wheel) have **no** `Direction`.
4. `CurveStrength` within `[0,1]`. `Text` ≤ 256 chars. Durations within the DSL bounds.
5. Gestures: no click-repeat + long-press together. A `complexShortcut` is a gesture action
   (usually `LongPressAction`), not a stick target; its trigger/execution/repeat combo is legal.
6. Precision key on the single-click position. Leader/Layer on the base layer. A layer mapping is
   not on its own leader's input. Only one temporary layer (`Layer: 1`).
7. Bundle: unique slots ≥ 1, embedded profiles valid, `SpecialKeys` entries are `specialKey` targets.
8. If you can run code: `ProfileSerializer.Deserialize` + `new MappingEngine(profile)` must succeed;
   `SchemeBundleCodec.Parse` must return `Succeeded`. Fix anything that throws.

## Pitfalls

- **Numbers for enums** — enums are names (`"Button": "A"`, not `0`). `KeyModifiers` combinations
  are `"Ctrl, Shift"`.
- **Missing `$type`** on a Source/Target → it becomes an unknown mapping (loads but does nothing).
- **`Direction` on a native pairing** is ignored; **missing `Direction` on a cross pairing** is rejected.
- **`CurveStrength > 1`** is clamped, not an error — but don't write it.
- **Layer > 1** folds to the single temporary layer; don't author multiple layers.
- Editing a live profile file under the running app: the daemon owns mapping; prefer importing via
  the GUI (导入方案) or stopping the app first. Unknown targets from newer builds are preserved, not lost.