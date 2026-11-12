namespace DeziroJoy.App.Services;

/// <summary>
/// Installs the per-user Inno Setup update on Windows. It launches the downloaded
/// <c>setup.exe</c> with the same silent flags the release workflow's verification uses
/// (<c>/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=…</c>, tools/Packaging/win/build-and-verify.ps1)
/// and waits for it: a non-zero exit - or a log whose tail says the install did not finish - is
/// an honest failure, never a success.
/// </summary>
/// <remarks>
/// <para>
/// The relaunch is deliberately NOT this installer's to make. The setup closes the running
/// DeziroJoy through the Restart Manager (<c>CloseApplications=yes</c> in DeziroJoy.iss), and the
/// process running this installer is one of the applications it closes - so an updater that
/// waited to relaunch the app itself would be terminated mid-install. Instead the relaunch comes
/// from the setup program's conditional <c>[Run]</c> entry, compiled only when the build passes
/// <c>/DDeziroJoyUpdateRun</c>, which is why <see cref="UpdateInstallResult.RelaunchHandled"/> is
/// true here.
/// </para>
/// <para>
/// Waiting for the exit code is still correct: if the Restart Manager closes this process before
/// the setup finishes, the conditional <c>[Run]</c> entry relaunches the app anyway, and the exit
/// code this installer never reads is simply a success it did not get to observe.
/// </para>
/// </remarks>
public sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    private readonly IProcessRunner _process;
    private readonly Func<string> _logPath;

    /// <summary>
    /// Internal so the process and log-path seams stay an implementation detail: production and
    /// the update host use the defaults, tests inject a fake runner and a known log path.
    /// </summary>
    internal WindowsUpdateInstaller(IProcessRunner? process = null, Func<string>? logPath = null)
    {
        _process = process ?? new ProcessRunner();
        _logPath = logPath ?? (static () => Path.Combine(
            Path.GetTempPath(),
            $"dezirojoy-setup-{Guid.NewGuid():N}.log"));
    }

    public bool Supports(InstallKind kind) => kind == InstallKind.WindowsInstall;

    public Task<UpdateInstallResult> InstallAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
    {
        var log = _logPath();
        var result = _process.Run(
            request.PackagePath,
            $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{log}\"");

        if (result.ExitCode == 0)
        {
            // The setup relaunches the app from its conditional [Run] entry; the Restart Manager
            // closes this updater mid-install, so the relaunch cannot be ours to make.
            return Task.FromResult(UpdateInstallResult.Ok(relaunchHandled: true));
        }

        return Task.FromResult(UpdateInstallResult.Failed(DescribeFailure(result, log)));
    }

    /// <summary>
    /// Why the setup did not finish. A process that never started means the downloaded setup is
    /// missing or not runnable; anything else ran and failed, and the tail of its Inno log is the
    /// most specific reason there is - quoted ahead of the generic "install it by hand" advice.
    /// </summary>
    private static string DescribeFailure(ProcessResult result, string log)
    {
        if (result.ExitCode < 0)
        {
            return $"无法启动 Windows 安装程序（{result.StandardError}）。请前往发布页手动下载安装。";
        }

        var tail = ReadLogTail(log);

        return string.IsNullOrEmpty(tail)
            ? $"Windows 安装程序未能完成更新（退出码 {result.ExitCode}）。请前往发布页手动下载安装。"
            : $"Windows 安装程序未能完成更新（退出码 {result.ExitCode}）：{tail}";
    }

    /// <summary>The tail of the Inno log: the failure sits near the end, and the whole log is far too long to quote.</summary>
    private static string ReadLogTail(string log)
    {
        try
        {
            if (!File.Exists(log))
            {
                return string.Empty;
            }

            var text = File.ReadAllText(log).Trim();

            return text.Length > LogTailLimit ? "…" + text[^LogTailLimit..] : text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be read (locked by a still-writing setup, say) must not turn a
            // clear non-zero exit into a crash: the exit code is the reason, the tail a bonus.
            return string.Empty;
        }
    }

    private const int LogTailLimit = 400;
}
