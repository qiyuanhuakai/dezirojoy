using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.Platform.Sdl3;

namespace DeziroJoy.App.ViewModels;

public sealed partial class DInputMappingViewModel : ObservableObject, IDisposable
{
    private readonly ConcurrentQueue<char> _commands = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<uint, (ISdl3JoystickReader? Reader, string? Error)> _openJoystick;
    private readonly Func<string, string, Sdl3MappingApplyResult> _applyMapping;
    private readonly Func<string, Sdl3MappingEntry, Sdl3MappingSaveResult> _saveMapping;
    private readonly IReadOnlyList<Sdl3MappingStep>? _steps;
    private readonly TimeSpan _pollInterval;
    private bool _started;
    private bool _finished;

    public DInputMappingViewModel(Sdl3JoystickInfo device)
        : this(
            device,
            instanceId =>
            {
                var opened = Sdl3JoystickCatalog.OpenJoystick(instanceId);
                return (opened.Reader, opened.Error);
            },
            Sdl3JoystickCatalog.ApplyMapping,
            Sdl3MappingFile.Save,
            Sdl3MappingFile.DefaultPath,
            null,
            TimeSpan.FromMilliseconds(16))
    {
    }

    internal DInputMappingViewModel(
        Sdl3JoystickInfo device,
        Func<uint, (ISdl3JoystickReader? Reader, string? Error)> openJoystick,
        Func<string, string, Sdl3MappingApplyResult> applyMapping,
        Func<string, Sdl3MappingEntry, Sdl3MappingSaveResult> saveMapping,
        string mappingPath,
        IReadOnlyList<Sdl3MappingStep>? steps,
        TimeSpan pollInterval)
    {
        Device = device;
        _openJoystick = openJoystick;
        _applyMapping = applyMapping;
        _saveMapping = saveMapping;
        MappingPath = mappingPath;
        _steps = steps;
        _pollInterval = pollInterval;
        ProgressText = "准备读取原始输入…";
    }

    public Sdl3JoystickInfo Device { get; }

    public string MappingPath { get; }

    public string DeviceSummary => $"{Device.Name} · {Device.ButtonCount} 按钮 / {Device.AxisCount} 轴 / {Device.HatCount} 方向帽";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCapture))]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCapture))]
    private bool _isCapturing;

    public bool CanCapture => IsCapturing;

    public event Action<bool>? CloseRequested;

    [RelayCommand]
    private void Skip() => _commands.Enqueue(Sdl3MappingCaptureRunner.SkipCommand);

    [RelayCommand]
    private void Cancel()
    {
        if (_finished)
        {
            CloseRequested?.Invoke(false);
            return;
        }

        _commands.Enqueue(Sdl3MappingCaptureRunner.CancelCommand);
    }

    public async Task<bool> RunAsync()
    {
        if (_started)
        {
            return false;
        }

        _started = true;
        IsCapturing = true;
        try
        {
            var opened = _openJoystick(Device.InstanceId);
            if (opened.Reader is null)
            {
                return Fail($"无法打开原始设备：{opened.Error ?? "未知错误"}");
            }

            using var reader = opened.Reader;
            var session = new Sdl3MappingCaptureSession(_steps);
            while (!session.IsComplete && !session.IsCancelled)
            {
                session.BeginStep(reader.Sample());
                Report(session);
                var settled = false;
                while (!settled && !session.IsCancelled)
                {
                    if (session.TryCapture(reader.Sample()))
                    {
                        settled = true;
                        continue;
                    }

                    if (_commands.TryDequeue(out var command))
                    {
                        if (command is Sdl3MappingCaptureRunner.CancelCommand)
                        {
                            session.Cancel();
                            break;
                        }

                        if (command is Sdl3MappingCaptureRunner.SkipCommand)
                        {
                            session.Skip();
                            settled = true;
                            continue;
                        }
                    }

                    if (_pollInterval > TimeSpan.Zero)
                    {
                        await Task.Delay(_pollInterval, _lifetime.Token);
                    }
                }
            }

            IsCapturing = false;
            if (session.IsCancelled)
            {
                StatusMessage = "已取消，未写入映射。";
                ProgressText = "已取消";
                _finished = true;
                CloseRequested?.Invoke(false);
                return false;
            }

            Report(session);
            var mapping = session.BuildMapping(Device.GuidString, Device.Name, Sdl3MappingPlatform.Resolve());
            var entry = Sdl3MappingFile.TryBuild(Device.GuidString, Device.Name, mapping)
                ?? throw new InvalidOperationException("生成的 SDL 映射未通过本地校验。");
            var applied = _applyMapping(entry.Mapping, entry.GuidString);
            if (!applied.Applied)
            {
                return Fail($"SDL 拒绝该映射，未保存：{applied.Error ?? "未知错误"}");
            }

            var saved = _saveMapping(MappingPath, entry);
            if (!saved.Saved)
            {
                return Fail(saved.Describe());
            }

            StatusMessage = session.UncoveredRequired.Count == 0
                ? "映射已保存。重启常驻服务后，设备将进入统一输入管线。"
                : $"映射已保存，但有 {session.UncoveredRequired.Count} 个必需输入未配置；这些输入不可用。";
            _finished = true;
            CloseRequested?.Invoke(true);
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Fail(exception.Message);
        }
        finally
        {
            IsCapturing = false;
        }
    }

    private void Report(Sdl3MappingCaptureSession session)
    {
        var current = session.Current;
        ProgressText = current is null
            ? session.DescribeProgress()
            : $"{session.DescribeProgress()}\n请操作对应控件；没有该输入时选择“跳过”。";
    }

    private bool Fail(string message)
    {
        StatusMessage = message;
        ProgressText = "映射未完成；请查看原因后选择“取消”关闭。";
        _finished = true;
        return false;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
