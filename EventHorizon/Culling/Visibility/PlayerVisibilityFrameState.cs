using System.Collections.Frozen;
using System.Linq;
using EventHorizon.Culling.Rules;

namespace EventHorizon.Culling.Visibility;

internal sealed record PlayerVisibilityFrameState
{
    public PlayerVisibilityFrameState(
        PlayerVisibilityTargetSet activeTarget,
        PlayerVisibilityReconciliation reconciliation,
        PlayerKeepBudgetStats budgetStats
    )
    {
        ActiveTarget = activeTarget;
        Reconciliation = reconciliation;
        BudgetStats = budgetStats;
        VisibleSlots = activeTarget
            .Targets.Where(static target => target.DesiredVisible)
            .Select(static target => (target.Identity, target.ObjectIndex))
            .ToFrozenSet();
    }

    public PlayerVisibilityTargetSet ActiveTarget { get; }
    public PlayerVisibilityReconciliation Reconciliation { get; }
    public PlayerKeepBudgetStats BudgetStats { get; }
    public FrozenSet<(PlayerObjectIdentity Identity, int ObjectIndex)> VisibleSlots { get; }
}
