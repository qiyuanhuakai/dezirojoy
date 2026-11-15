using DeziroJoy.App.Ipc;

namespace DeziroJoy.App.Services;

/// <summary>Small control client used by the repeatable baseline runner; never starts a GUI.</summary>
internal static class PerformanceControl
{
    public static bool TryRun(IReadOnlyList<string> args, out int exitCode)
    {
        exitCode = 0;
        if (args.Count == 0 || args[0] != "--perf-control")
        {
            return false;
        }

        if (args.Count != 2 || args[1] is not ("start" or "stop" or "keyboard-open" or "keyboard-close" or "shutdown"))
        {
            Console.Error.WriteLine("用法：DeziroJoy --perf-control start|stop|keyboard-open|keyboard-close|shutdown");
            exitCode = 2;
            return true;
        }

        exitCode = RunAsync(args[1]).GetAwaiter().GetResult();
        return true;
    }

    private static async Task<int> RunAsync(string command)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            IDaemonEndpoint endpoint = command == "start"
                ? new NamedPipeDaemonEndpoint()
                : new ProbeOnlyDaemonEndpoint();
            await using var controller = new DaemonController(
                endpoint,
                startupGrace: TimeSpan.FromMilliseconds(100));
            await controller.ConnectAsync(timeout.Token);
            var result = command switch
            {
                "start" => await controller.StartAsync(timeout.Token),
                "stop" => await controller.StopAsync(timeout.Token),
                "keyboard-open" => await controller.SetScreenKeyboardAsync(true, timeout.Token),
                "keyboard-close" => await controller.SetScreenKeyboardAsync(false, timeout.Token),
                "shutdown" => await controller.ShutdownDaemonAsync(timeout.Token),
                _ => throw new InvalidOperationException(command),
            };

            if (result.Outcome != MutationOutcome.Accepted)
            {
                Console.Error.WriteLine($"控制命令被拒绝：{result.ErrorCode} {result.ErrorMessage}");
                return 1;
            }

            Console.WriteLine($"{command}: accepted");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"控制命令失败：{exception.Message}");
            return 1;
        }
    }
}
