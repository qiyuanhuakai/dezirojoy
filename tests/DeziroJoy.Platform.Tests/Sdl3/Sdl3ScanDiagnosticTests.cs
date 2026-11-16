using DeziroJoy.Platform.Sdl3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// The scan diagnostic is what a user reads when the device table is empty or partial, so
/// each outcome must carry its own actionable guidance - the three zero-gamepad causes
/// (no SDL runtime, nothing recognized, open failed) need three different fixes.
/// </summary>
public sealed class Sdl3ScanDiagnosticTests
{
    [Fact]
    public void Sdl_Unavailable_Names_The_Missing_Runtime_And_Its_Error()
    {
        var diagnostic = new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.SdlUnavailable,
            GamepadCount = 0,
            InitError = "找不到原生 SDL3 运行时",
        };

        Assert.Equal("SDL3 运行时不可用，无法检测手柄", diagnostic.Hint);
        Assert.Contains("SDL3.dll", diagnostic.Guidance);
        Assert.Contains("libSDL3.so", diagnostic.Guidance);
        Assert.Contains("找不到原生 SDL3 运行时", diagnostic.Guidance);
    }

    [Fact]
    public void No_Gamepads_Explains_That_Unmapped_Devices_Are_Not_Standard_Pads()
    {
        var diagnostic = new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.NoGamepads,
            GamepadCount = 0,
        };

        Assert.Contains("没有识别为标准 Gamepad", diagnostic.Hint);
        Assert.Contains("补充映射", diagnostic.Guidance);
    }

    [Fact]
    public void Open_Failures_Are_Listed_With_The_Instance_Id_And_Sdl_Error()
    {
        var diagnostic = new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.OpenFailed,
            GamepadCount = 0,
            OpenFailures = [new Sdl3OpenFailure(7, "没有可用映射")],
        };

        Assert.Contains("无法打开", diagnostic.Hint);
        Assert.Contains("#7", diagnostic.Guidance);
        Assert.Contains("没有可用映射", diagnostic.Guidance);
    }

    [Fact]
    public void A_Successful_Scan_Has_No_Hint_Or_Guidance()
    {
        var diagnostic = new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.GamepadsFound,
            GamepadCount = 2,
        };

        Assert.Empty(diagnostic.Hint);
        Assert.Empty(diagnostic.Guidance);
    }

    [Fact]
    public void A_Partial_Scan_Keeps_The_Success_And_Lists_The_Failure()
    {
        // One working pad plus one that failed to open is an operational success - but the
        // failure must not disappear from the guidance.
        var diagnostic = new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.GamepadsFound,
            GamepadCount = 1,
            OpenFailures = [new Sdl3OpenFailure(8, "打开失败")],
        };

        Assert.Empty(diagnostic.Hint);
        Assert.Contains("#8", diagnostic.Guidance);
        Assert.Contains("打开失败", diagnostic.Guidance);
    }

    [Fact]
    public void Unstable_Identities_Are_Called_Out_Next_To_The_Outcome_Guidance()
    {
        var diagnostic = new Sdl3ScanDiagnostic
        {
            Outcome = Sdl3ScanOutcome.GamepadsFound,
            GamepadCount = 1,
            UnstableIdentityInstanceIds = [3, 4],
        };

        Assert.Contains("#3", diagnostic.Guidance);
        Assert.Contains("#4", diagnostic.Guidance);
        Assert.Contains("重启", diagnostic.Guidance);
    }

    [Fact]
    public void The_Runtime_Version_Is_Rendered_As_Major_Minor_Micro()
    {
        var runtime = new Sdl3RuntimeInfo(3004020, "SDL-3.4.20-release");

        Assert.Equal("3.4.20", runtime.VersionText);
    }
}
