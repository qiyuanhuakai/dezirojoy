using System.Runtime.CompilerServices;
using DeziroJoy.App.Services;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// Test-host configuration for the injection mutex.
///
/// Production resolves one machine-wide name so the GUI and the resident daemon contend
/// for a single slot. The test host overrides that with a per-instance name, because
/// tests construct hosts directly, xUnit runs collections in parallel, and one GUI test
/// (<see cref="Views.TrayGuiTests.The_Engine_Keeps_Running_While_The_Window_Is_Hidden"/>)
/// deliberately leaves an engine running for the whole process - sharing the production
/// name would serialise the suite on one slot, and that leaked hold would fail unrelated
/// tests and block the daemon process test.
///
/// Tests that assert exclusion pass their own shared name explicitly.
/// </summary>
internal static class InjectionMutexTestHostConfiguration
{
    [ModuleInitializer]
    public static void UsePerInstanceMutexNames()
        => EngineHost.DefaultInjectionMutexNameResolver =
            static () => $"{InjectionMutex.DefaultName}.Tests.{Guid.NewGuid():N}";
}
