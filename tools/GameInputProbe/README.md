# GameInput v4 验证程序

这是一个独立的 Windows x64 原生探针，不属于 `DeziroJoy.slnx`，也**没有接入当前产品的
XInput provider**。它用于在决定正式接入范围前，按设备核实 GameInput v4 的实际行为。

固定依赖：`Microsoft.GameInput` **4.0.306**（`PackageReference`）。

## 验证范围

- 调用 `GameInputCreate`，报告初始化 HRESULT；
- 打印实际加载的 `GameInput.dll` / `GameInputRedist.dll` 路径，并区分系统／inbox 与
  app-local（side-by-side）部署；
- 调用 `IGameInput::EnableBackgroundInput(true)`，供失焦输入测试；
- 注册 v4 设备回调，输出连接、断开和 `GameInputDeviceEventXInputIndexUpdated`；
- 输出设备名称、VID／PID、支持的输入种类、支持的 Guide／Share、ContainerId、PnP 路径；
- 通过 `IGameInputDevice::GetXInputDeviceIndex` 输出与 XInput 槽位的关联；
- 轮询 `GameInputKindSystemButton` reading，通过 `GetSystemButtonState` 只打印 Guide／Share
  的按下、松开沿。

探针不注入键鼠、不修改配置、不安装运行时。默认运行 60 秒，也可用 `--seconds N` 指定
1–86400 秒；`--help` 查看用法。

## 构建

需要 Visual Studio 2022 Build Tools（C++ 工作负载、v143、Windows SDK）和 MSBuild。

```powershell
msbuild tools/GameInputProbe/GameInputProbe.vcxproj `
  /restore /p:Configuration=Release /p:Platform=x64
```

运行：

```powershell
.\tools\GameInputProbe\bin\Release\GameInputProbe.exe --seconds 60
```

### 随应用部署运行时

```powershell
msbuild tools/GameInputProbe/GameInputProbe.vcxproj `
  /restore /p:Configuration=Release /p:Platform=x64 /p:GameInputAppLocal=true
```

此开关会把包内 x64 `GameInputRedist.dll` 和 `GameInputRawInputProxy.exe` 复制到 exe 同目录。
比较两种部署时，应分别使用干净的输出目录；若同目录留有 app-local DLL，加载器可能优先选它，
不能把那次运行当作“仅系统运行时”的结果。

## 真机验收步骤

1. 连接目标手柄后启动探针，确认出现 `[device]`，记录名称、VID／PID、ContainerId 和
   `systemButtons`。
2. 分别按下并松开 Guide、Share，确认出现对应 `[system-button] ... DOWN/UP`；没有 Share
   的设备只记录实际存在的能力，不把“无事件”误判为接口失败。
3. 切到另一个窗口，使控制台失焦，再重复按键。比较前台与后台事件是否一致。
4. 运行中断开、重连手柄，确认 `[event] connected/disconnected`；多手柄交换顺序时观察
   `xinput-index-updated` 与 `xinputIndex`。
5. 分别用系统安装运行时和 app-local 运行时重复上述步骤，保存完整输出，写明设备型号、
   连接方式（USB／蓝牙）、Windows 版本和运行时路径。

**app-local 限制**：Microsoft 的部署文档明确说明，PC 上随应用运行时的
`EnableBackgroundInput(true)` **没有效果**，因为该运行时不能认领系统按钮；输入焦点也不由
GameInput 服务统一管理。v4 的系统按钮 reading 本身仍可用于 app-local。需要可靠后台行为时，
应安装 redistributable，并分别验证两种部署。

官方资料：

- [GameInput sideloading](https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/input/overviews/input-agility-redistributable)
- [Microsoft.GameInput NuGet](https://www.nuget.org/packages/Microsoft.GameInput/4.0.306)

## 本机当前结果（2026-11-15）

| 项目 | 结果 |
|---|---|
| Release x64 编译 | 通过，使用 4.0.306 包内 v4 头文件与库 |
| 仅系统运行时 | `GameInputCreate` 返回 `0x80004002`（`E_NOINTERFACE`），当前系统运行时未提供请求的 v4 接口 |
| app-local 4.0.306 | 初始化成功，运行时路径识别正确，后台开关已调用，2 秒轮询正常结束 |
| 设备事件／Guide／Share／后台行为／XInput 槽位关联 | **未做真机实测**；本次轮询未观察到设备事件 |

因此，当前证据只支持“探针已编译、app-local 运行时可初始化”，不支持“DeziroJoy 已支持
Guide／Share”或“所有第三方手柄都能提供这些按钮”的结论。
