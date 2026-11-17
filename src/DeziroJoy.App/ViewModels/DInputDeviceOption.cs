using DeziroJoy.Platform.Sdl3;

namespace DeziroJoy.App.ViewModels;

public sealed record DInputDeviceOption(Sdl3JoystickInfo Device)
{
    public string DisplayName => $"{Device.Name} · {Device.ButtonCount} 按钮 / {Device.AxisCount} 轴 / {Device.HatCount} 方向帽";
}
