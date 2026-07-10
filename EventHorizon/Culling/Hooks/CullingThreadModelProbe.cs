using System;
using System.Threading;
using Dalamud.Plugin.Services;

namespace EventHorizon.Culling.Hooks;

internal sealed class CullingThreadModelProbe
{
#if DEBUG
    private readonly IPluginLog log;
    private int frameworkDepth;
    private int detourDepth;
    private int frameworkThreadId;
    private int detourThreadId;
    private int frameworkThreadChanged;
    private int detourThreadChanged;
    private int overlapObserved;
    private int detourReentryObserved;
    private long nextSummaryTick;
#endif

    public CullingThreadModelProbe(IPluginLog log)
    {
#if DEBUG
        this.log = log;
#else
        _ = log;
#endif
    }

#if DEBUG
    public void EnterFramework()
    {
        ObserveThread(ref frameworkThreadId, ref frameworkThreadChanged);
        Interlocked.Increment(ref frameworkDepth);
        if (Volatile.Read(ref detourDepth) != 0)
        {
            Interlocked.Exchange(ref overlapObserved, 1);
        }
    }

    public void ExitFramework()
    {
        Interlocked.Decrement(ref frameworkDepth);
        LogSummaryIfDue();
    }

    public void EnterDetour()
    {
        ObserveThread(ref detourThreadId, ref detourThreadChanged);
        if (Interlocked.Increment(ref detourDepth) > 1)
        {
            Interlocked.Exchange(ref detourReentryObserved, 1);
        }

        if (Volatile.Read(ref frameworkDepth) != 0)
        {
            Interlocked.Exchange(ref overlapObserved, 1);
        }
    }

    public void ExitDetour() => Interlocked.Decrement(ref detourDepth);

    private void LogSummaryIfDue()
    {
        var now = Environment.TickCount64;
        var due = Volatile.Read(ref nextSummaryTick);
        if (now < due || Interlocked.CompareExchange(ref nextSummaryTick, now + 10_000, due) != due)
        {
            return;
        }

        log.Debug(
            "[CullingThreadModel] frameworkThread={FrameworkThread} detourThread={DetourThread} frameworkChanged={FrameworkChanged} detourChanged={DetourChanged} overlap={Overlap} detourReentry={DetourReentry}",
            Volatile.Read(ref frameworkThreadId),
            Volatile.Read(ref detourThreadId),
            Volatile.Read(ref frameworkThreadChanged) != 0,
            Volatile.Read(ref detourThreadChanged) != 0,
            Volatile.Read(ref overlapObserved) != 0,
            Volatile.Read(ref detourReentryObserved) != 0
        );
    }

    private static void ObserveThread(ref int firstThreadId, ref int changed)
    {
        var current = Environment.CurrentManagedThreadId;
        var first = Volatile.Read(ref firstThreadId);
        if (first == 0)
        {
            first = Interlocked.CompareExchange(ref firstThreadId, current, 0);
            if (first == 0)
            {
                return;
            }
        }

        if (first != current)
        {
            Interlocked.Exchange(ref changed, 1);
        }
    }
#else
    public void EnterFramework() { }

    public void ExitFramework() { }

    public void EnterDetour() { }

    public void ExitDetour() { }
#endif
}
