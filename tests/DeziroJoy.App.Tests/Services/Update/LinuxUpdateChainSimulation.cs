using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// One execution of the installer's generated chain: the stubs that were invoked, in order,
/// with their arguments, plus the log the chain wrote and the environment the relaunched app
/// saw.
/// </summary>
internal sealed class ChainRun
{
    private readonly List<(string Stub, List<string> Arguments)> _invocations;

    internal ChainRun(
        int exitCode,
        List<(string Stub, List<string> Arguments)> invocations,
        string log,
        Dictionary<string, string> appEnvironment,
        string standardOutput,
        string standardError)
    {
        ExitCode = exitCode;
        _invocations = invocations;
        Log = log;
        AppEnvironment = appEnvironment;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    /// <summary>The chain's own exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Everything the chain appended to its log.</summary>
    public string Log { get; }

    /// <summary>The relaunched app's environment, as the drop handed it over.</summary>
    public Dictionary<string, string> AppEnvironment { get; }

    /// <summary>What the chain printed; kept for a failing assertion to quote.</summary>
    public string StandardOutput { get; }

    /// <summary>What the chain complained about; kept for a failing assertion to quote.</summary>
    public string StandardError { get; }

    /// <summary>The stubs that were invoked, in order.</summary>
    public IReadOnlyList<string> Calls => _invocations.Select(call => call.Stub).ToList();

    public bool WasCalled(string stub) => _invocations.Any(call => call.Stub == stub);

    /// <summary>The arguments of the one invocation of <paramref name="stub"/>.</summary>
    public List<string> Arguments(string stub)
    {
        var calls = _invocations.Where(call => call.Stub == stub).ToList();

        if (calls.Count != 1)
        {
            throw new InvalidOperationException(
                $"expected exactly one {stub} invocation, found {calls.Count}");
        }

        return calls[0].Arguments;
    }

    /// <summary>The arguments of the <paramref name="index"/>th invocation of <paramref name="stub"/>.</summary>
    public List<string> Invocation(string stub, int index)
    {
        var calls = _invocations.Where(call => call.Stub == stub).ToList();

        if (index >= calls.Count)
        {
            throw new InvalidOperationException(
                $"expected at least {index + 1} {stub} invocations, found {calls.Count}");
        }

        return calls[index].Arguments;
    }
}

/// <summary>
/// Runs the Linux package installer's generated chain for real, against stub executables:
/// pkexec, runuser, setpriv, systemctl and the package managers are all fake scripts in a
/// temporary directory that is the only thing on PATH, so the real polkit dialog, the real
/// privilege drop and the real package manager are unreachable by construction. The stubs
/// record every invocation - one header line with their name, then one tab-indented line per
/// argument - and the relaunched app records the environment it was handed.
/// <para>
/// The simulation is the only way to prove the generated shell behaves: which identity each
/// step ran as, which environment travelled with it, that a missing unit or a failed install
/// does not block the relaunch, and that a dismissed polkit dialog still brings the app back.
/// It runs on Linux only - the chain is a POSIX shell script, and on Windows there is nothing
/// to execute it with.
/// </para>
/// </summary>
internal sealed class ChainSimulation : IDisposable
{
    /// <summary>Records one invocation: the stub's name, then its arguments, one per line.</summary>
    private const string RecordInvocation =
        "printf '%s\\n' '%NAME%' >>\"$DEZIROJOY_STUB_RECORDS\"\n"
        + "for __arg in \"$@\"; do printf '\\t%s\\n' \"$__arg\" >>\"$DEZIROJOY_STUB_RECORDS\"; done\n";

    private const string RunUserStub =
        """
        #!/bin/sh
        %PROLOGUE%
        while [ $# -gt 0 ]; do
            if [ "$1" = "--" ]; then
                shift
                break
            fi
            shift
        done
        exec "$@"
        """;

    private const string SetPrivStub =
        """
        #!/bin/sh
        %PROLOGUE%
        while [ $# -gt 0 ]; do
            case "$1" in
                --reuid|--regid) shift 2 ;;
                --init-groups|--clear-groups|--keep-groups|--no-new-privs) shift ;;
                --) shift; break ;;
                *) break ;;
            esac
        done
        exec "$@"
        """;

    private const string PkExecStub =
        """
        #!/bin/sh
        %PROLOGUE%
        if [ -n "$DEZIROJOY_STUB_PKEXEC_EXIT" ]; then
            printf 'pkexec stub: authentication dismissed\n' >&2
            exit "$DEZIROJOY_STUB_PKEXEC_EXIT"
        fi
        exec "$@"
        """;

    private const string InstallerStub =
        """
        #!/bin/sh
        %PROLOGUE%
        printf '%s stub: package installed\n' '%NAME%'
        exit "${DEZIROJOY_STUB_INSTALL_EXIT:-0}"
        """;

    private const string SystemCtlStub =
        """
        #!/bin/sh
        %PROLOGUE%
        printf 'systemctl stub: unit state answered\n'
        exit "${DEZIROJOY_STUB_SYSTEMCTL_EXIT:-0}"
        """;

    private const string IdStub =
        """
        #!/bin/sh
        %PROLOGUE%
        # The simulation's user is a fixture, not a machine account: a deterministic uid and
        # gid keep the setpriv branch testable on a runner that has no such user, where the
        # real id would fail and leave the chain unable to drop privileges at all.
        case "$1" in
            -u) printf '1000\n' ;;
            -g) printf '1000\n' ;;
            *) printf 'uid=1000(qiyuaner) gid=1000(qiyuaner) groups=1000(qiyuaner),1001(input)\n' ;;
        esac
        exit 0
        """;

    private const string AppStub =
        """
        #!/bin/sh
        %PROLOGUE%
        env >"$DEZIROJOY_STUB_APP_ENV"
        exit 0
        """;

    private readonly string _root;
    private readonly string _stubDirectory;
    private readonly string _toolDirectory;
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);

    internal ChainSimulation(
        bool withRunUser = true,
        bool withSetPriv = true,
        int systemCtlExit = 0,
        int installExit = 0,
        int? pkExecExit = null)
    {
        _root = Path.Combine(Path.GetTempPath(), $"dezirojoy-chain-sim-{Guid.NewGuid():N}");
        _stubDirectory = Path.Combine(_root, "stubs");
        _toolDirectory = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_stubDirectory);
        Directory.CreateDirectory(_toolDirectory);

        AppPath = Path.Combine(_stubDirectory, "DeziroJoy");
        LogDirectory = Path.Combine(_root, "logs");

        WriteStub("pkexec", WithPrologue(PkExecStub, "pkexec"));
        WriteStub("apt-get", WithPrologue(InstallerStub, "apt-get"));
        WriteStub("dnf", WithPrologue(InstallerStub, "dnf"));
        WriteStub("systemctl", WithPrologue(SystemCtlStub, "systemctl"));
        WriteStub("id", WithPrologue(IdStub, "id"));
        WriteStub("DeziroJoy", WithPrologue(AppStub, "app"));

        if (withRunUser)
        {
            WriteStub("runuser", WithPrologue(RunUserStub, "runuser"));
        }

        if (withSetPriv)
        {
            WriteStub("setpriv", WithPrologue(SetPrivStub, "setpriv"));
        }

        // The only real executables the chain is allowed to reach: the shell it runs under
        // and the coreutils its script calls that are not themselves under test. Everything
        // else resolves to a stub or to nothing, which is what makes the no-drop-tool
        // scenario possible at all.
        foreach (var tool in new[] { "sh", "env", "sleep" })
        {
            File.CreateSymbolicLink(Path.Combine(_toolDirectory, tool), ResolveTool(tool));
        }

        if (pkExecExit is not null)
        {
            _variables["DEZIROJOY_STUB_PKEXEC_EXIT"] = pkExecExit.Value.ToString(
                CultureInfo.InvariantCulture);
        }

        if (systemCtlExit != 0)
        {
            _variables["DEZIROJOY_STUB_SYSTEMCTL_EXIT"] = systemCtlExit.ToString(
                CultureInfo.InvariantCulture);
        }

        if (installExit != 0)
        {
            _variables["DEZIROJOY_STUB_INSTALL_EXIT"] = installExit.ToString(
                CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Where the installer is told to write its artifacts for this run.</summary>
    internal string LogDirectory { get; }

    /// <summary>The stub standing in for <c>/opt/dezirojoy/DeziroJoy</c>.</summary>
    internal string AppPath { get; }

    /// <summary>
    /// The session environment the simulation hands the installer, in the order the installer
    /// carries it - the fixture the test asserts travelled into the user context.
    /// </summary>
    internal static List<string> ExpectedEnvironment() =>
    [
        "HOME=/home/qiyuaner",
        "USER=qiyuaner",
        "LOGNAME=qiyuaner",
        "XDG_CONFIG_HOME=/home/qiyuaner/.config",
        "XDG_DATA_HOME=/home/qiyuaner/.local/share",
        "XDG_RUNTIME_DIR=/run/user/1000",
        "DISPLAY=:0",
        "XAUTHORITY=/home/qiyuaner/.Xauthority",
        "DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus",
    ];

    /// <summary>
    /// Executes the chain the installer launched: the recorded start info is rebuilt with the
    /// hermetic PATH and the stub controls, run to completion, and its records collected.
    /// </summary>
    internal async Task<ChainRun> ExecuteAsync(
        ProcessStartInfo launched,
        CancellationToken cancellationToken)
    {
        var recordsPath = Path.Combine(_root, "records.txt");
        var appEnvironmentPath = Path.Combine(_root, "app-environment.txt");
        _variables["DEZIROJOY_STUB_RECORDS"] = recordsPath;
        _variables["DEZIROJOY_STUB_APP_ENV"] = appEnvironmentPath;

        // The outer script is a fixed two-argument sh -c invocation; anything else is the
        // installer handing the chain over in a shape this simulation does not model.
        Assert.Equal("sh", Path.GetFileName(launched.FileName));
        Assert.Equal(2, launched.ArgumentList.Count);
        Assert.Equal("-c", launched.ArgumentList[0]);

        var outerPath = Path.Combine(_root, "outer.sh");
        File.WriteAllText(outerPath, launched.ArgumentList[1]);
        CheckSyntax(outerPath);

        var scriptPath = Assert.Single(Directory.GetFiles(LogDirectory, "*.sh"));
        CheckSyntax(scriptPath);

        var startInfo = new ProcessStartInfo("sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(launched.ArgumentList[1]);

        // A hermetic PATH: the stubs and the handful of real tools, nothing else. The real
        // pkexec, systemctl and apt-get are unreachable by construction.
        startInfo.EnvironmentVariables["PATH"] =
            $"{_stubDirectory}{Path.PathSeparator}{_toolDirectory}";

        foreach (var (name, value) in _variables)
        {
            startInfo.EnvironmentVariables[name] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("the chain could not be started");

        var exited = await WaitForExitAsync(process, TimeSpan.FromSeconds(60), cancellationToken)
            .ConfigureAwait(false);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("the chain did not finish within its budget");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        // The relaunch is backgrounded, so the app's record can land after the chain's own
        // exit; the app's environment file follows it.
        await WaitForAsync(
            () => File.Exists(appEnvironmentPath),
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        var invocations = ParseInvocations(File.Exists(recordsPath)
            ? File.ReadAllLines(recordsPath)
            : []);
        var log = File.ReadAllText(Assert.Single(Directory.GetFiles(LogDirectory, "*.log")));
        var appEnvironment = File.Exists(appEnvironmentPath)
            ? ParseEnvironment(File.ReadAllLines(appEnvironmentPath))
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return new ChainRun(
            process.ExitCode,
            invocations,
            log,
            appEnvironment,
            stdout,
            stderr);

        static void CheckSyntax(string path)
        {
            var check = new ProcessStartInfo("sh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            check.ArgumentList.Add("-n");
            check.ArgumentList.Add(path);

            using var syntax = Process.Start(check)
                ?? throw new InvalidOperationException("sh could not be started");
            var error = syntax.StandardError.ReadToEnd();
            syntax.WaitForExit();

            Assert.True(
                syntax.ExitCode == 0,
                $"the generated script does not parse ({path}): {error}");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string WithPrologue(string stub, string name)
        => stub
            .Replace("%PROLOGUE%", RecordInvocation, StringComparison.Ordinal)
            .Replace("%NAME%", name, StringComparison.Ordinal);

    private void WriteStub(string name, string content)
    {
        var path = Path.Combine(_stubDirectory, name);
        File.WriteAllText(path, content);

        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string ResolveTool(string name)
    {
        foreach (var directory in new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin" })
        {
            var candidate = Path.Combine(directory, name);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"the chain simulation needs {name} on this machine and could not find it");
    }

    private static List<(string Stub, List<string> Arguments)> ParseInvocations(string[] lines)
    {
        var invocations = new List<(string, List<string>)>();

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (line.StartsWith('\t'))
            {
                Assert.NotEmpty(invocations);
                invocations[^1].Item2.Add(line[1..]);
            }
            else
            {
                invocations.Add((line, []));
            }
        }

        return invocations;
    }

    private static Dictionary<string, string> ParseEnvironment(string[] lines)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var separator = line.IndexOf('=');

            if (separator > 0)
            {
                environment[line[..separator]] = line[(separator + 1)..];
            }
        }

        return environment;
    }

    private static async Task<bool> WaitForExitAsync(
        Process process,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task WaitForAsync(
        Func<bool> condition,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (!condition())
        {
            if (elapsed.Elapsed >= budget)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }
    }
}
