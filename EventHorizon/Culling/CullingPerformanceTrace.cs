namespace EventHorizon.Culling;

internal readonly record struct CullingPerformanceTrace(
    bool IsRefresh,
    bool RefreshPlayerPreview,
    int ActionCount,
    int PendingShowCount,
    int PendingHideCount,
    long TotalTicks,
    long GuardTicks,
    long KeepPlanTicks,
    long VisibilityPlanTicks,
    long ReconcileTicks,
    long PreviewTicks,
    CullingTickPerformanceTrace Tick
)
{
    public bool HasValue => TotalTicks > 0;
}

internal readonly record struct CullingTickPerformanceTrace(
    int ActionCount,
    long TotalTicks,
    long PlayerActionsTicks,
    long NonPlayerTicks,
    long PruneHiddenTicks,
    long PruneFadesTicks,
    long HiddenVfxTicks
)
{
    public bool HasValue => TotalTicks > 0;
}
