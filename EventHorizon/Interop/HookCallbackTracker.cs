using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Plugin.Services;

namespace EventHorizon.Interop;

internal sealed class HookCallbackTracker
{
    private const int StateInitializing = 0;
    private const int StateReady = 1;
    private const int StateStopping = 2;
    private const int StateStopped = 3;

    private readonly string name;
    private readonly IPluginLog log;
    private readonly int ownerManagedThreadId = Environment.CurrentManagedThreadId;
    private readonly uint ownerNativeThreadId = NativeThread.GetCurrentThreadId();
    private readonly ConcurrentDictionary<uint, byte> observedNativeThreads = [];

    private int state = StateInitializing;
    private int activeCallbacks;
    private int maxActiveCallbacks;
    private int lastObservedNativeThreadId;
    private int loggedConcurrentCallbacks;
    private int loggedInitializingEntry;
    private int loggedStoppingEntry;
    private int loggedMissingOriginal;

    public HookCallbackTracker(string name, IPluginLog log)
    {
        this.name = name;
        this.log = log;
        SafeDebug(
            "[Hook:{HookName}] tracker created. nativeThread={NativeThreadId} managedThread={ManagedThreadId}",
            name,
            ownerNativeThreadId,
            ownerManagedThreadId
        );
    }

    public bool ShouldRunPluginLogic => Volatile.Read(ref state) == StateReady;

    public CallbackScope Enter()
    {
        var nativeThreadId = NativeThread.GetCurrentThreadId();
        var nativeThreadIdBits = unchecked((int)nativeThreadId);
        if (Volatile.Read(ref lastObservedNativeThreadId) != nativeThreadIdBits)
        {
            Volatile.Write(ref lastObservedNativeThreadId, nativeThreadIdBits);
            if (observedNativeThreads.TryAdd(nativeThreadId, 0))
            {
                SafeDebug(
                    "[Hook:{HookName}] observed callback thread. nativeThread={NativeThreadId} managedThread={ManagedThreadId} ownerNativeThread={OwnerNativeThreadId}",
                    name,
                    nativeThreadId,
                    Environment.CurrentManagedThreadId,
                    ownerNativeThreadId
                );
            }
        }

        var active = Interlocked.Increment(ref activeCallbacks);
        UpdateMaximum(ref maxActiveCallbacks, active);
        if (active > 1 && Interlocked.Exchange(ref loggedConcurrentCallbacks, 1) == 0)
        {
            SafeWarning("[Hook:{HookName}] concurrent or reentrant callbacks observed. activeCallbacks={ActiveCallbacks}", name, active);
        }

        var currentState = Volatile.Read(ref state);
        if (currentState == StateInitializing && Interlocked.Exchange(ref loggedInitializingEntry, 1) == 0)
        {
            SafeWarning(
                "[Hook:{HookName}] callback entered while the hook was still initializing. activeCallbacks={ActiveCallbacks}",
                name,
                active
            );
        }
        else if (currentState >= StateStopping && Interlocked.Exchange(ref loggedStoppingEntry, 1) == 0)
        {
            SafeWarning(
                "[Hook:{HookName}] callback overlapped hook shutdown. state={State} activeCallbacks={ActiveCallbacks}",
                name,
                currentState,
                active
            );
        }

        return new CallbackScope(this);
    }

    public void MarkReady()
    {
        Volatile.Write(ref state, StateReady);
        SafeDebug("[Hook:{HookName}] ready.", name);
    }

    public void BeginStop()
    {
        var previousState = Interlocked.Exchange(ref state, StateStopping);
        SafeDebug(
            "[Hook:{HookName}] stopping. previousState={PreviousState} activeCallbacks={ActiveCallbacks}",
            name,
            previousState,
            Volatile.Read(ref activeCallbacks)
        );
    }

    public bool WaitForDrain(TimeSpan timeout)
    {
        var quietPeriod = TimeSpan.FromMilliseconds(25);
        var stopwatch = Stopwatch.StartNew();
        TimeSpan? quietSince = null;

        while (stopwatch.Elapsed < timeout)
        {
            if (Volatile.Read(ref activeCallbacks) == 0)
            {
                quietSince ??= stopwatch.Elapsed;
                if (stopwatch.Elapsed - quietSince.Value >= quietPeriod)
                {
                    return true;
                }
            }
            else
            {
                quietSince = null;
            }

            Thread.Sleep(1);
        }

        var remaining = Volatile.Read(ref activeCallbacks);
        SafeWarning(
            "[Hook:{HookName}] timed out waiting for callbacks to drain. remaining={Remaining} timeoutMs={TimeoutMs}",
            name,
            remaining,
            timeout.TotalMilliseconds
        );
        return false;
    }

    public void MarkStopped()
    {
        Volatile.Write(ref state, StateStopped);
        SafeDebug(
            "[Hook:{HookName}] stopped. observedThreads={ObservedThreads} maxActiveCallbacks={MaxActiveCallbacks}",
            name,
            observedNativeThreads.Count,
            Volatile.Read(ref maxActiveCallbacks)
        );
    }

    public void ReportMissingOriginal()
    {
        if (Interlocked.Exchange(ref loggedMissingOriginal, 1) != 0)
        {
            return;
        }

        SafeWarning("[Hook:{HookName}] callback entered before the original delegate was available.", name);
    }

    public void ReportException(Exception exception, string operation)
    {
        try
        {
            log.Error(exception, "[Hook:{HookName}] operation failed: {Operation}", name, operation);
        }
        catch
        {
            // Never allow logging failures to escape a native callback.
        }
    }

    private void Exit()
    {
        Interlocked.Decrement(ref activeCallbacks);
    }

    private void SafeDebug(string messageTemplate, params object[] values)
    {
        try
        {
            log.Debug(messageTemplate, values);
        }
        catch
        {
            // Diagnostics must not affect hook execution.
        }
    }

    private void SafeWarning(string messageTemplate, params object[] values)
    {
        try
        {
            log.Warning(messageTemplate, values);
        }
        catch
        {
            // Diagnostics must not affect hook execution.
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    public readonly struct CallbackScope : IDisposable
    {
        private readonly HookCallbackTracker? tracker;

        internal CallbackScope(HookCallbackTracker tracker)
        {
            this.tracker = tracker;
        }

        public void Dispose()
        {
            tracker?.Exit();
        }
    }

    private static class NativeThread
    {
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
    }
}
