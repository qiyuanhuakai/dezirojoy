namespace DeziroJoy.App.Services;

/// <summary>
/// Raised when the engine refuses to start because another process already holds the
/// injection mutex. Starting anyway would run a second engine that injects every input
/// twice, so the start fails clearly instead.
/// </summary>
public sealed class InjectionAlreadyActiveException : InvalidOperationException
{
    public InjectionAlreadyActiveException(string message)
        : base(message)
    {
    }

    public InjectionAlreadyActiveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The named, system-wide mutex that guarantees exactly one process injects mapped input
/// at a time. The engine holds it for exactly as long as it is injecting: acquired when
/// the mapping loop starts, released when the loop ends - which covers
/// <see cref="EngineHost.Stop"/>, <see cref="EngineHost.Dispose"/> and every other
/// teardown path, because they all end the loop.
///
/// Two layers, because the platforms differ:
///
/// - The named <see cref="Mutex"/> is the cross-process layer. On Windows a
///   <c>Global\</c> name is machine-wide across sessions, so the GUI and the elevated
///   resident daemon - different processes, possibly different integrity levels -
///   contend for one slot. On Linux .NET named mutexes are per-user session, which is
///   exactly the daemon-vs-GUI scope there: the resident service is a Windows scheduled
///   task, so on Linux the GUI is the only injector and a session-scoped slot is enough.
/// - A process-local registry is the in-process layer, because .NET named mutexes on
///   Linux do NOT exclude two handles in the same process (they are per-process file
///   locks), so a second <see cref="EngineHost"/> in this process would otherwise
///   acquire happily on Linux while Windows refused it.
///
/// A holder that dies without releasing does not block the next start: on Linux the
/// kernel drops the file lock with the process; on Windows the mutex is abandoned and
/// the next waiter is granted it, which .NET surfaces as
/// <see cref="AbandonedMutexException"/> - treated here as a successful acquire.
/// </summary>
public sealed class InjectionMutex : IDisposable
{
    /// <summary>
    /// The name production coordinates on. <c>Global\</c> so the GUI and the elevated
    /// resident daemon contend for one machine-wide slot on Windows.
    /// </summary>
    public const string DefaultName = "Global\\DeziroJoy.Injection";

    private static readonly Dictionary<string, InjectionMutex> ProcessHeld = new(StringComparer.Ordinal);

    private static readonly Lock ProcessGate = new();

    private readonly string _name;

    private readonly Mutex _mutex;

    private bool _released;

    private InjectionMutex(string name, Mutex mutex)
    {
        _name = name;
        _mutex = mutex;
    }

    /// <summary>
    /// Tries to become the single injector under <paramref name="name"/>. Returns null
    /// when another process - or another host in this process - already holds it; the
    /// caller must then refuse to start rather than run a second engine.
    /// </summary>
    public static InjectionMutex? TryAcquire(string name)
    {
        lock (ProcessGate)
        {
            if (ProcessHeld.ContainsKey(name))
            {
                return null;
            }

            var mutex = new Mutex(false, name);

            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The previous holder died mid-hold. The wait succeeded and ownership
                // transferred to us; the exception only reports the abandonment.
                owned = true;
            }

            if (!owned)
            {
                mutex.Dispose();
                return null;
            }

            var acquired = new InjectionMutex(name, mutex);
            ProcessHeld[name] = acquired;
            return acquired;
        }
    }

    /// <summary>
    /// Releases the slot. Safe to call more than once. Must be called from the thread
    /// that acquired it: .NET mutex ownership is per-thread on every platform, which is
    /// why <see cref="EngineHost"/> acquires and releases on its loop thread.
    /// </summary>
    public void Release()
    {
        lock (ProcessGate)
        {
            if (_released)
            {
                return;
            }

            _released = true;
            ProcessHeld.Remove(_name);
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    public void Dispose() => Release();
}
