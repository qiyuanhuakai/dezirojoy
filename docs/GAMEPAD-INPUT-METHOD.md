# 手柄输入法方案调研

本文件调研可接入 DeziroJoy 的手柄文本输入（"手柄输入法"）方案，并给出推荐。
范围只涉及"用手柄往任意窗口输入文字"这一件事，不涉及映射引擎本身。

## 结论摘要

**推荐：应用内自绘屏幕键盘，复用现有注入路径。**
- 网格键盘 + 焦点管理放在 Avalonia 主界面内，字符通过现有 `IOutputSink`
  （Windows `SendInputOutputSink`、Linux `UInputOutputSink`）注入；
- Windows 用 `KEYEVENTF_UNICODE` 作为普通文本框的兜底通道，游戏目标继续走
  现有 VK + scancode 路径；
- Linux 用 uinput keycode，配剪贴板粘贴兜底；
- 中文分阶段：先做 ASCII/符号，再做"驱动目标 IME"的拼音层，最后才考虑内置 RIME。

**明确不推荐**：操作系统屏幕键盘集成、Steamworks 文本输入、SDL2、
GPL 的 libpinyin。理由见后文。

## 一、方案一：应用内自绘屏幕键盘

### 结构

典型实现分四层：

1. **焦点层**：键盘窗口必须不抢走目标窗口焦点。Avalonia 用
   `Window.ShowActivated = false` + `Topmost`；注入由不激活的窗口触发，
   前台仍是目标应用。
2. **网格层**：普通 Avalonia 控件即可（Button 网格 + 手柄导航），
   没有技术难度。
3. **IME/候选层**：若要中文，需要 composing 区 + 候选列表 + 分页。
   Android 的 Game Text Input 是当前平台里做得最完整的参考设计
   （`composingRegion` + `stateBuffer` + `GameTextInput` 事件流）。
4. **注入层**：见第四节。

### 真实参考项目

| 项目 | 技术 | 许可 | 说明 |
| --- | --- | --- | --- |
| [rubujo/InputBox](https://github.com/rubujo/InputBox) | C# / WinForms | CC0 | Windows 掌机上给 FF14 等游戏做中文输入：启动 `TabTip.exe` + XInput 导航 + 剪贴板粘贴。**与 DeziroJoy 场景最接近的真实参考** |
| [CodeRedCatto/MiKeys](https://github.com/CodeRedCatto/MiKeys) | C# / MonoGame | MIT | 手柄驱动的虚拟键盘，6 star |
| [greenpixels/greenpixels-onscreen-keyboard](https://github.com/greenpixels/greenpixels-onscreen-keyboard) | GDScript / Godot | MIT | Godot 实现，手柄+触屏 |
| [Avalonia VirtualKeyboard](https://docs.avaloniaui.net/controls/input/text-input/virtualkeyboard/) | Avalonia 官方 | 商业 | **属 Avalonia Pro（€899/年/席）**，提供 `VirtualKeyboard` 控件、布局系统与主题包；插件体系支持接入 RIME |

Avalonia 官方键盘是本次调研最重要的发现：官方实现存在，但被划入
Avalonia Pro 商业订阅。对一个要保持轻量、跨平台、可自托管的开源项目，
自绘网格键盘（几百行 XAML + 手柄导航）比引入年费订阅更合适；若未来中文
成为主要诉求，可评估 Pro 或自接 RIME。

### 陷阱

- **焦点偷取**：键盘窗口一旦激活，`SendInput`/uinput 的注入目标就变成键盘
  自己。必须 `ShowActivated = false`，且注入发生在目标仍是前台时。
- **UIPI / 完整性级别**：非提权进程无法向提权目标窗口注入。DeziroJoy 已有
  常驻提权 daemon 与 `docs/ELEVATION-AND-DAEMON.md` 描述的架构，这一条已经被
  解决；新的文本注入应复用同一 daemon，而不是新建提权路径。
- **游戏目标**：走 Raw Input / DirectInput 的游戏读 scancode，
  `KEYEVENTF_UNICODE` 与部分合成事件对它们无效。这是整个工具已知的边界，
  不是输入法特有。
- **IME 组合态污染**：目标应用 IME 正处于 composing 状态时注入按键，会把
  按键并进组合串。实现上不应假设 IME 空闲。

## 二、方案二：复用操作系统屏幕键盘

### Windows

| 手段 | 可用性 | 说明 |
| --- | --- | --- |
| `TabTip.exe` | 部分可用 | 现代触控键盘。启动可靠；**隐藏不可靠**——公开途径只有结束进程或向窗口发 `WM_SYSCOMMAND / SC_CLOSE` |
| `ITipInvocation.Toggle` | 不可用于生产 | 未公开 COM（`CLSID 4ce576fa-83dc-4f88-951c-9d0782b4e376`），[1809 之后失效](https://github.com/maximcus/WPFTabTip) |
| `EnableDesktopModeAutoInvoke` | 不可靠 | 注册表项让编辑框获得焦点时自动唤起，[1803 之后回归不可靠](https://stackoverflow.com/questions/38774139) |
| `osk.exe` | 遗留 | XP 时代遗留，鼠标导向，无手柄导航 |

其他已被验证的问题：TabTip 是 UWP 应用，**运行在中完整性级别，无法向
提权窗口输入**（[ReplaceOSK](https://github.com/Lulech23/ReplaceOSK)）；
非 US 键盘布局下 TabTip 会输出错误字符
（[Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/5848198/windows-11-shows-2-touch-screen-keyboards)）。

### Linux

| 实现 | 状态 | 可编程控制 |
| --- | --- | --- |
| GNOME Shell 内置 OSK | 活跃 | **无 D-Bus 接口，不能命令行唤起** |
| [squeekboard](https://github.com/droidian/squeekboard) | 活跃 | Wayland，D-Bus `sm.puri.OSK0.SetVisible`；layer-shell + `virtual-keyboard-v1` + `input-method-v2` |
| [wvkbd](https://github.com/jjsullivan5196/wvkbd) | 半活跃 | `SIGUSR1`/`SIGUSR2` 显示/隐藏；`--auto` 配合 `input_method_v2` 可做 IME |
| [onboard](https://github.com/onboard-osk/onboard) | 半活跃 | 主要 X11，Wayland 为实验性；有 D-Bus 接口 |
| maliit | 不活跃 | KDE 集成为主 |
| caribou | **已归档**（2019） | 死路 |
| matchbox-keyboard | 嵌入式，2016 后基本停滞 | 死路 |

Wayland 的 `virtual-keyboard-v1` 协议**只传 keycode，不传 keysym**
（[协议规范](https://hoyon.github.io/wayland-protocol-docs/protocols/virtual_keyboard_unstable_v1.html)）。

### 结论

操作系统屏幕键盘路线跨平台不可行：Windows 与 Linux 的可用实现、控制接口、
生命周期语义都不同，且都做不好手柄导航。**不推荐。**

## 三、方案三：现有库 / SDK

| 方案 | 状态 | 判断 |
| --- | --- | --- |
| Steamworks `ISteamUtils::ShowGamepadTextInput` / `ShowFloatingGamepadTextInput` | 可用但绑死 Steam | 需要 Steam 运行 + overlay 开启；[浮动键盘仅 Steam Deck 可用](https://stackoverflow.com/questions/70025000/)；[官方 API](https://partner.steamgames.com/doc/api/ISteamUtils)。**不适合独立分发的开源应用** |
| Android Game Text Input | 平台原生，仅 Android | [官方文档](https://developer.android.com/games/agdk/add-support-for-text-input)。**作为设计参考极佳，桌面不可用** |
| Google Play Games Input SDK | 无文本输入 API | [官方文档](https://developer.android.com/games/playgames/input-sdk) 只有键鼠重映射；Unity 包已归档 |
| SDL2 / SDL3 `SDL_StartTextInput` | 桌面基本 no-op | [SDL2 文档](https://wiki.libsdl.org/SDL2/SDL_StartTextInput)：无平台键盘时静默返回。为文本输入引入 SDL 太重 |
| RimeSharp（librime 绑定） | 可用，生态薄 | [RimeSharp](https://github.com/rimeinn/RimeSharp)，1 star，Apache-2.0，net8 + net472，Windows/Linux。librime 本身 BSD-3、成熟 |
| libpinyin | GPL-3.0 | [libpinyin](https://github.com/libpinyin/libpinyin)。**许可证与宽松开源项目冲突，排除** |
| SunPinyin | 老化 | 维护停滞 |

**结论：.NET 生态没有成熟的手柄输入法。** 唯一可用的中文引擎绑定是
RimeSharp，但社区采用度极低，等于自担维护风险。

## 四、CJK 专项

### Windows

- **`KEYEVENTF_UNICODE`（推荐兜底）**：[SendInput 官方文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)。
  绕过 IME 直接插入 Unicode，稳定可靠；代理对需按高低项分别发送
  （[讨论](https://stackoverflow.com/questions/50420514/sendinput-wont-send-basic-unicode-to-some-windows)）。
  缺点：对 Raw Input 游戏无效，且拿不到候选词。
- **合成按键进目标 IME**：可行，但候选窗口导航困难——候选框是独立窗口，
  手柄只能映射到数字键/方向键，体验割裂。
- **`ImmSetCompositionString` 直改组合串**：需要目标窗口 HIMC，跨进程基本
  不可行（[官方文档](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immsetcompositionstringw)）。
  死路。
- 相关坑：注入 CJK/表情时 `VK_PACKET` 路由有已知问题
  （[分析](https://linlingo.billlinch.com/en/keyboard-hook-and-ime.html)）。

### Linux

- **uinput 只发 keycode**：[ydotool 在非 US 布局下输出乱码](https://github.com/VocaHQ/vocalinux/issues/164)。
  中文只能靠目标 IME 配合，或临时改 keymap。
- **Wayland 两条路**：`virtual-keyboard-v1` 上传自有 keymap（wtype 的做法，
  仅 wlroots 系合成器支持）；或 `input-method-v2` 直接提交 UTF-8
  （GNOME/KDE 支持程度不一）。
- **剪贴板粘贴兜底**：最可靠，但会覆盖用户剪贴板，且游戏常拦截粘贴。

### 质量天花板

| 路线 | 上限 |
| --- | --- |
| 直接 Unicode 注入 | 可靠，但无候选词、无拼音 |
| 驱动目标 IME | 候选质量取决于用户 IME，但候选导航脆弱 |
| 内置拼音引擎（RIME） | 质量最高，工程量最大（词典、候选排序、平台绑定） |

## 五、推荐与分期

### 主推：应用内自绘网格键盘 + 复用注入路径

与 DeziroJoy 现有架构的契合点：

1. `IOutputSink` 抽象已经存在（`src/DeziroJoy.Platform/Sinks/`），
   新增文本注入是同一抽象的扩展，不需要新的提权路径；
2. Windows 侧 `SendInputOutputSink` 已经做 VK + `MapVirtualKeyW` scancode
   转换与 UIPI 处理，文本通道可以并排扩展；
3. 提权 daemon 已解决 UIPI，键盘窗口保持非激活即可。

### 分期

- **第一阶段**：ASCII / 符号 / 数字网格键盘。注入走现有 VK 路径，
  Windows 加 `KEYEVENTF_UNICODE` 兜底。工作量小，立刻可用。
- **第二阶段**：中文拼音层——网格键盘输出拼音字母（scancode），
  交给目标 IME 组合；手柄映射数字键选候选。不引入任何依赖。
- **第三阶段（可选）**：若中文成为主要诉求，评估接入 RIME
  （自绘候选 UI + librime），或评估 Avalonia Pro 的 VirtualKeyboard。

### 不推荐

- 操作系统屏幕键盘集成（跨平台不可行、生命周期不可控、无手柄导航）；
- Steamworks 文本输入（绑死 Steam 分发）；
- SDL2（为一个次要功能引入重量级依赖）；
- libpinyin（GPL 与宽松许可冲突）。

## 参考链接

- Steamworks `ISteamUtils`：https://partner.steamgames.com/doc/api/ISteamUtils
- Steam 浮动键盘仅 Deck：https://stackoverflow.com/questions/70025000/
- TabTip 隐藏不可靠：https://github.com/maximcus/WPFTabTip
- TabTip 注册表失效：https://stackoverflow.com/questions/38774139
- TabTip 提权窗口问题：https://github.com/Lulech23/ReplaceOSK
- 双键盘问题：https://learn.microsoft.com/en-us/answers/questions/5848198/windows-11-shows-2-touch-screen-keyboards
- squeekboard：https://github.com/droidian/squeekboard
- wvkbd：https://github.com/jjsullivan5196/wvkbd
- onboard：https://github.com/onboard-osk/onboard
- Wayland 虚拟键盘调研：https://github.com/hoverth/wayland-virtual-keyboards
- `virtual-keyboard-v1` 协议：https://hoyon.github.io/wayland-protocol-docs/protocols/virtual_keyboard_unstable_v1.html
- GNOME 内置 OSK 无独立入口：https://discourse.gnome.org/t/how-can-i-call-the-systems-built-in-on-screen-keyboard-using-a-command-in-ubuntu/27676
- caribou 归档：https://mail.gnome.org/archives/gnome-infrastructure/2019-January/msg00015.html
- matchbox-keyboard：https://tracker.debian.org/pkg/matchbox-keyboard
- Android Game Text Input：https://developer.android.com/games/agdk/add-support-for-text-input
- Android GTI API：https://developer.android.com/reference/games/game-text-input/group/game-text-input
- GPG Input SDK：https://developer.android.com/games/playgames/input-sdk
- SDL `SDL_StartTextInput`：https://wiki.libsdl.org/SDL2/SDL_StartTextInput
- Avalonia VirtualKeyboard：https://docs.avaloniaui.net/controls/input/text-input/virtualkeyboard/
- Avalonia Pro 定价：https://avaloniaui.net/pricing
- Avalonia 文本输入与 IME：https://docs.avaloniaui.net/docs/input-interaction/text-input
- `SendInput`：https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput
- Unicode 注入讨论：https://stackoverflow.com/questions/50420514/sendinput-wont-send-basic-unicode-to-some-windows
- `ImmSetCompositionString`：https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immsetcompositionstringw
- 组合串：https://learn.microsoft.com/en-us/windows/win32/intl/composition-string
- IME 与键盘钩子：https://linlingo.billlinch.com/en/keyboard-hook-and-ime.html
- librime：https://github.com/rime/librime
- RimeSharp：https://github.com/rimeinn/RimeSharp
- libpinyin：https://github.com/libpinyin/libpinyin
- wtype：https://github.com/atx/wtype
- wdotool（uinput keycode 限制）：https://crates.io/crates/wdotool/0.1.3
- ydotool 布局问题：https://github.com/VocaHQ/vocalinux/issues/164
- InputBox：https://github.com/rubujo/InputBox
- MiKeys：https://github.com/CodeRedCatto/MiKeys
- greenpixels OSK：https://github.com/greenpixels/greenpixels-onscreen-keyboard
