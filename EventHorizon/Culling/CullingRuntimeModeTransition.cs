namespace EventHorizon.Culling;

internal sealed class CullingRuntimeModeTransition
{
    public CullingRuntimeMode? Current { get; private set; }

    public CullingRuntimeTransition Synchronize(CullingRuntimeMode next)
    {
        if (Current == next)
        {
            return new(next, Changed: false, EnterInactive: false, RebuildActive: false, ClearLongTermRules: false);
        }

        Current = next;
        return new(
            next,
            Changed: true,
            EnterInactive: next != CullingRuntimeMode.Active,
            RebuildActive: next == CullingRuntimeMode.Active,
            ClearLongTermRules: next == CullingRuntimeMode.Disabled
        );
    }
}

internal readonly record struct CullingRuntimeTransition(
    CullingRuntimeMode Mode,
    bool Changed,
    bool EnterInactive,
    bool RebuildActive,
    bool ClearLongTermRules
);
