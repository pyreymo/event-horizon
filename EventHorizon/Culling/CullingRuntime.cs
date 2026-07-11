namespace EventHorizon.Culling;

internal enum CullingRuntimeMode
{
    Disabled,
    PlayerUnavailable,
    SuspendedDuty,
    SuspendedLowPlayerCount,
    Active,
}

internal readonly record struct CullingFrameSchedule(bool Refresh, bool Tick)
{
    public static CullingFrameSchedule Decide(CullingRuntimeMode mode, bool refreshDue, bool topologyDirty) =>
        mode == CullingRuntimeMode.Active
            ? new CullingFrameSchedule(refreshDue || topologyDirty, Tick: true)
            : new CullingFrameSchedule(Refresh: false, Tick: false);
}

internal readonly record struct CullingRuntimeSynchronization(CullingRuntimeMode Mode, bool RequiresRefresh);

internal sealed class CullingRuntimeModeTransition
{
    public CullingRuntimeMode? Current { get; private set; }

    public CullingRuntimeTransition Synchronize(CullingRuntimeMode next)
    {
        if (Current == next)
        {
            return new(next, Changed: false, EnterInactive: false, RebuildActive: false);
        }

        Current = next;
        return new(next, Changed: true, EnterInactive: next != CullingRuntimeMode.Active, RebuildActive: next == CullingRuntimeMode.Active);
    }
}

internal readonly record struct CullingRuntimeTransition(CullingRuntimeMode Mode, bool Changed, bool EnterInactive, bool RebuildActive);
