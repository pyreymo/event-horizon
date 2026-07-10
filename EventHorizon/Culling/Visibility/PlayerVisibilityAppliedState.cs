using System;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerVisibilityAppliedState
{
    public PlayerVisibilityTargetSet? ActiveTarget { get; private set; }

    public void SetActiveTarget(PlayerVisibilityTargetSet activeTarget)
    {
        ArgumentNullException.ThrowIfNull(activeTarget);
        ActiveTarget = activeTarget;
    }

    public bool IsExplicitlyVisible(PlayerObjectIdentity identity)
    {
        if (ActiveTarget == null)
        {
            return false;
        }

        foreach (var target in ActiveTarget.Targets)
        {
            if (target.Identity == identity && target.DesiredVisible)
            {
                return true;
            }
        }

        return false;
    }

    public void Clear() => ActiveTarget = null;
}
