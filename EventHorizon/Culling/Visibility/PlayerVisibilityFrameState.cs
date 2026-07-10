using System.Collections.Frozen;
using System.Linq;
using EventHorizon.Culling.Rules;

namespace EventHorizon.Culling.Visibility;

internal sealed record PlayerVisibilityFrameState
{
    public PlayerVisibilityFrameState(
        PlayerVisibilityTargetSet activeTarget,
        PlayerVisibilityReconciliation reconciliation,
        PlayerKeepBudgetStats budgetStats,
        PlayerVisibilitySelectionTrace selectionTrace
    )
    {
        ActiveTarget = activeTarget;
        Reconciliation = reconciliation;
        BudgetStats = budgetStats;
        SelectionTrace = selectionTrace;
        VisibleSlots = activeTarget
            .Targets.Where(static target => target.DesiredVisible)
            .Select(static target => (target.Identity, target.ObjectIndex))
            .ToFrozenSet();
    }

    public PlayerVisibilityTargetSet ActiveTarget { get; }
    public PlayerVisibilityReconciliation Reconciliation { get; }
    public PlayerKeepBudgetStats BudgetStats { get; }
    public PlayerVisibilitySelectionTrace SelectionTrace { get; }
    public FrozenSet<(PlayerObjectIdentity Identity, int ObjectIndex)> VisibleSlots { get; }
}
