using EventHorizon.Culling.Visibility;

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
    CullingTickPerformanceTrace Tick,
    CullingPreviewPerformanceTrace Preview = default,
    PlayerVisibilityClassificationCounts PlayerVisibilityClasses = default,
    PlayerVisibilityShadowTrace Shadow = default
)
{
    public bool HasValue => TotalTicks > 0;
}

internal readonly record struct CullingPreviewPerformanceTrace(int EntryCount, long BeginTicks, long AddTicks, long BuildTicks)
{
    public bool HasValue => EntryCount > 0 || BeginTicks > 0 || AddTicks > 0 || BuildTicks > 0;
}

internal readonly record struct CullingTickPerformanceTrace(
    int ActionCount,
    long TotalTicks,
    long PlayerActionsTicks,
    long NonPlayerTicks,
    long PruneHiddenTicks,
    long PruneFadesTicks,
    long HiddenVfxTicks,
    CullingHiddenVfxPerformanceTrace HiddenVfx = default
)
{
    public bool HasValue => TotalTicks > 0;
}

internal readonly record struct CullingHiddenVfxPerformanceTrace(
    int HiddenCount,
    int VisibleCount,
    int ActiveCount,
    int ShowCreatedCount,
    int ShowUpdatedCount,
    int ShowSkippedCount,
    int ShowRemovedCount,
    int ShowDeferredCount,
    long CollectTicks,
    long ProjectTicks,
    long ShowTicks,
    long PruneTicks,
    long ClearTicks
)
{
    public bool HasValue =>
        HiddenCount > 0
        || VisibleCount > 0
        || ActiveCount > 0
        || ShowCreatedCount > 0
        || ShowUpdatedCount > 0
        || ShowSkippedCount > 0
        || ShowRemovedCount > 0
        || ShowDeferredCount > 0
        || CollectTicks > 0
        || ProjectTicks > 0
        || ShowTicks > 0
        || PruneTicks > 0
        || ClearTicks > 0;
}
