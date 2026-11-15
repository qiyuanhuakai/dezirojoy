# DeziroJoy config — worked examples

Companion to `SKILL.md`. These are valid, loadable shapes. GUIDs are illustrative — generate
fresh ones when authoring. Omitted fields take their defaults.

## 1. Minimal profile — a few buttons and the right stick as mouse

```json
{
  "SchemaVersion": 3,
  "Name": "入门",
  "DeviceId": null,
  "Layout": "Xbox",
  "PresetSlot": 1,
  "Mappings": [
    { "Id": "11111111-1111-1111-1111-111111111111",
      "Source": { "$type": "button", "Button": "A" },
      "Target": { "$type": "key", "Key": "Space" } },
    { "Id": "22222222-2222-2222-2222-222222222222",
      "Source": { "$type": "button", "Button": "X" },
      "Target": { "$type": "combo", "Modifiers": "Ctrl", "Key": "C" } },
    { "Id": "33333333-3333-3333-3333-333333333333",
      "Source": { "$type": "stick", "Stick": "Right" },
      "Target": { "$type": "mouseMove",
        "Settings": { "MaxSpeed": 1600, "DeadZone": 0.15, "Curve": "Precise", "CurveStrength": 1 } } },
    { "Id": "44444444-4444-4444-4444-444444444444",
      "Source": { "$type": "stick", "Stick": "Left" },
      "Target": { "$type": "wheel", "Settings": { "MaxTicksPerSecond": 12 } } }
  ]
}
```

Note: stick→mouseMove/wheel carry **no** `Direction`; button→key/combo carry **no** `Direction`.

## 2. Cross pairings need a Direction

```json
{ "Id": "55555555-5555-5555-5555-555555555555",
  "Source": { "$type": "stick", "Stick": "Left", "Direction": "Up" },
  "Target": { "$type": "key", "Key": "ArrowUp" } }
```
```json
{ "Id": "66666666-6666-6666-6666-666666666666",
  "Source": { "$type": "button", "Button": "RightBumper", "Direction": "Right" },
  "Target": { "$type": "mouseMove", "Settings": { "MaxSpeed": 900 } } }
```

## 3. Gestures: single / double / long-press on one button

B = Enter on click, Ctrl+Z on double-click, a program on long-press:

```json
{ "Id": "77777777-7777-7777-7777-777777777777",
  "Source": { "$type": "button", "Button": "B",
    "Gesture": {
      "DoubleClickWindow": 0.25,
      "LongPressThreshold": 0.5,
      "DoubleClickAction": { "$type": "combo", "Modifiers": "Ctrl", "Key": "Z" },
      "LongPressAction": { "$type": "complexShortcut", "Script": "hold Alt\nevery 1s tap Tab" }
    } },
  "Target": { "$type": "key", "Key": "Enter" } }
```

The mapping's own `Target` is the single-click action. `LongPressAction` here is a program that
holds Alt and taps Tab each second while held (default trigger=longpress, run=whileheld).

## 4. Precision key + Leader (preset) + Leader (temporary layer)

```json
{ "Id": "88888888-8888-8888-8888-888888888888",
  "Source": { "$type": "button", "Button": "LeftStick" },
  "Target": { "$type": "specialKey", "Kind": "Precision", "PrecisionMode": "Hold" } }
```
```json
{ "Id": "99999999-9999-9999-9999-999999999999",
  "Source": { "$type": "button", "Button": "Back" },
  "Target": { "$type": "specialKey", "Kind": "Leader", "SwitchMode": "Press", "SlotA": 1, "SlotB": 2 } }
```
```json
{ "Id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "Source": { "$type": "button", "Button": "RightBumper" },
  "Target": { "$type": "specialKey", "Kind": "Leader", "SwitchMode": "Hold", "Action": "Layer" } }
```
A mapping on the temporary layer (overrides X while RB is held; everything else inherits base):
```json
{ "Id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "Source": { "$type": "button", "Button": "X" },
  "Target": { "$type": "combo", "Modifiers": "Alt", "Key": "V" },
  "Layer": 1 }
```
The layer leader (RB) is on the base layer; the layer mapping (X) is not on RB. Only one layer.

## 5. Text output (search box / rename / short URL)

```json
{ "Id": "cccccccc-cccc-cccc-cccc-cccccccccccc",
  "Source": { "$type": "button", "Button": "Y" },
  "Target": { "$type": "text", "Text": "https://github.com/" } }
```

## 6. Complex shortcut DSL variants

Run once on the press edge (a macro):
```
trigger press
run once
tap Ctrl
tap C
```
Toggle on re-press, ignore re-triggers while running:
```
trigger press
run toggle
repeat ignore
hold Shift
```
Release trigger must run once:
```
trigger release
run once
tap Escape
```
As a JSON target (headers may also live in the Script; fields shown are the defaults):
```json
{ "$type": "complexShortcut", "Script": "trigger press\nrun once\ntap Ctrl\ntap C" }
```

## 7. Scheme bundle (two presets + shared special keys + a layer)

```json
{
  "SchemaVersion": 1,
  "Name": "办公 + 游戏",
  "AppVersion": "0.0.2-beta.4-preview",
  "Notes": "预设1 桌面办公；预设2 游戏。Back 切换预设，RB 进入临时层。",
  "Presets": [
    { "Slot": 1,
      "Profile": { "SchemaVersion": 3, "Name": "桌面办公", "Layout": "Xbox", "PresetSlot": 1,
        "Mappings": [
          { "Id": "d1d1d1d1-d1d1-d1d1-d1d1-d1d1d1d1d1d1",
            "Source": { "$type": "button", "Button": "X" },
            "Target": { "$type": "combo", "Modifiers": "Ctrl", "Key": "C" } },
          { "Id": "d2d2d2d2-d2d2-d2d2-d2d2-d2d2d2d2d2d2",
            "Source": { "$type": "button", "Button": "Back" },
            "Target": { "$type": "specialKey", "Kind": "Leader", "SwitchMode": "Press", "SlotA": 1, "SlotB": 2 } },
          { "Id": "d3d3d3d3-d3d3-d3d3-d3d3-d3d3d3d3d3d3",
            "Source": { "$type": "button", "Button": "RightBumper" },
            "Target": { "$type": "specialKey", "Kind": "Leader", "SwitchMode": "Hold", "Action": "Layer" } },
          { "Id": "d4d4d4d4-d4d4-d4d4-d4d4-d4d4d4d4d4d4",
            "Source": { "$type": "button", "Button": "Y" },
            "Target": { "$type": "combo", "Modifiers": "Ctrl", "Key": "V" }, "Layer": 1 }
        ] },
      "ActionNames": [ { "Source": "X 键", "Action": "复制" }, { "Source": "Y 键", "Action": "粘贴" } ] },
    { "Slot": 2,
      "Profile": { "SchemaVersion": 3, "Name": "游戏", "Layout": "Xbox", "PresetSlot": 2,
        "Mappings": [
          { "Id": "e1e1e1e1-e1e1-e1e1-e1e1-e1e1e1e1e1e1",
            "Source": { "$type": "stick", "Stick": "Left" },
            "Target": { "$type": "mouseMove", "Settings": { "MaxSpeed": 2000 } } }
        ] },
      "ActionNames": [ { "Source": "左摇杆", "Action": "鼠标移动" } ] }
  ],
  "SpecialKeys": null,
  "Layers": [ { "LayerId": 1, "PresetSlot": 1, "Actions": [ { "Source": "Y 键", "Action": "粘贴" } ] } ]
}
```

## 8. Foreground rules

```json
{
  "Enabled": true,
  "DefaultSlot": 1,
  "Rules": [
    { "Id": "f1f1f1f1-f1f1-f1f1-f1f1-f1f1f1f1f1f1",
      "ProcessName": "chrome", "Priority": 10, "Action": "Profile", "Slot": 1 },
    { "Id": "f2f2f2f2-f2f2-f2f2-f2f2-f2f2f2f2f2f2",
      "ProcessName": "code", "Priority": 10, "Action": "Profile", "Slot": 1 },
    { "Id": "f3f3f3f3-f3f3-f3f3-f3f3-f3f3f3f3f3f3",
      "ProcessName": "eldenring", "Priority": 20, "Action": "NativeGamepad" },
    { "Id": "f4f4f4f4-f4f4-f4f4-f4f4-f4f4f4f4f4f4",
      "ProcessName": "*", "Priority": 0, "Action": "Profile", "Slot": 1 }
  ]
}
```
`eldenring` gets the pad directly (mapping paused); browsers/editors use preset 1; anything else
falls to the `*` rule / desktop default.