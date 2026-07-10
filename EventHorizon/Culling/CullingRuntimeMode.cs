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
