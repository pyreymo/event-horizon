using System;
using System.Collections.Generic;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerVisibilityReconciler
{
    private readonly List<PlayerVisibilityIntent> toShow = [];
    private readonly List<PlayerVisibilityIntent> toHide = [];
    private readonly List<PlayerVisibilityIntent> unchanged = [];
    private readonly List<PlayerVisibilityAction> actions = [];

    public PlayerVisibilityReconciliation Reconcile(PlayerVisibilityPlan plan, HiddenObjectTracker hiddenObjectTracker)
    {
        toShow.Clear();
        toHide.Clear();
        unchanged.Clear();
        actions.Clear();

        var appliedVisibleCount = 0;
        var desiredVisibleCount = 0;

        foreach (var intent in plan.Intents)
        {
            var appliedVisible = !hiddenObjectTracker.IsHidden(intent.Identity);
            if (appliedVisible)
            {
                appliedVisibleCount++;
            }

            if (intent.DesiredVisible)
            {
                desiredVisibleCount++;
            }

            if (intent.DesiredVisible && !appliedVisible)
            {
                toShow.Add(intent);
            }
            else if (!intent.DesiredVisible && appliedVisible)
            {
                toHide.Add(intent);
            }
            else
            {
                unchanged.Add(intent);
            }
        }

        toShow.Sort(CompareShowPriority);
        toHide.Sort(CompareHidePriority);

        AddTransitions(actions, toShow, toHide);
        AddMaintainedVisibility(actions, unchanged);

        return new PlayerVisibilityReconciliation(
            plan.Revision,
            actions,
            desiredVisibleCount,
            appliedVisibleCount,
            toShow.Count,
            toHide.Count
        );
    }

    private static void AddTransitions(
        List<PlayerVisibilityAction> actions,
        IReadOnlyList<PlayerVisibilityIntent> toShow,
        IReadOnlyList<PlayerVisibilityIntent> toHide
    )
    {
        var swapCount = Math.Min(toShow.Count, toHide.Count);
        for (var index = 0; index < swapCount; index++)
        {
            actions.Add(PlayerVisibilityAction.Swap(toHide[index], toShow[index]));
        }

        for (var index = swapCount; index < toHide.Count; index++)
        {
            actions.Add(PlayerVisibilityAction.Hide(toHide[index], PlayerVisibilityActionReason.TargetReduced));
        }

        for (var index = swapCount; index < toShow.Count; index++)
        {
            actions.Add(PlayerVisibilityAction.Show(toShow[index], PlayerVisibilityActionReason.TargetExpanded));
        }
    }

    private static void AddMaintainedVisibility(List<PlayerVisibilityAction> actions, IReadOnlyList<PlayerVisibilityIntent> unchanged)
    {
        foreach (var intent in unchanged)
        {
            actions.Add(
                intent.DesiredVisible
                    ? PlayerVisibilityAction.Show(intent, PlayerVisibilityActionReason.Maintain)
                    : PlayerVisibilityAction.Hide(intent, PlayerVisibilityActionReason.Maintain)
            );
        }
    }

    private static int CompareShowPriority(PlayerVisibilityIntent left, PlayerVisibilityIntent right)
    {
        var rankComparison = left.Decision.Rank.CompareTo(right.Decision.Rank);
        if (rankComparison != 0)
        {
            return rankComparison;
        }

        var distanceComparison = left.Decision.TieBreaker.DistanceSq.CompareTo(right.Decision.TieBreaker.DistanceSq);
        if (distanceComparison != 0)
        {
            return distanceComparison;
        }

        var entityComparison = left.Identity.EntityId.CompareTo(right.Identity.EntityId);
        return entityComparison != 0 ? entityComparison : left.Identity.Address.ToInt64().CompareTo(right.Identity.Address.ToInt64());
    }

    private static int CompareHidePriority(PlayerVisibilityIntent left, PlayerVisibilityIntent right) => CompareShowPriority(right, left);
}

internal sealed record PlayerVisibilityReconciliation(
    int Revision,
    IReadOnlyList<PlayerVisibilityAction> Actions,
    int DesiredVisibleCount,
    int AppliedVisibleCount,
    int PendingShowCount,
    int PendingHideCount
);

internal readonly record struct PlayerVisibilityAction(
    PlayerVisibilityActionKind Kind,
    PlayerVisibilityActionReason Reason,
    PlayerVisibilityIntent Intent,
    PlayerVisibilityIntent? PairedIntent
)
{
    public static PlayerVisibilityAction Show(PlayerVisibilityIntent intent, PlayerVisibilityActionReason reason) =>
        new(PlayerVisibilityActionKind.Show, reason, intent, null);

    public static PlayerVisibilityAction Hide(PlayerVisibilityIntent intent, PlayerVisibilityActionReason reason) =>
        new(PlayerVisibilityActionKind.Hide, reason, intent, null);

    public static PlayerVisibilityAction Swap(PlayerVisibilityIntent outgoing, PlayerVisibilityIntent incoming) =>
        new(PlayerVisibilityActionKind.Swap, PlayerVisibilityActionReason.Swap, incoming, outgoing);
}

internal enum PlayerVisibilityActionKind
{
    Show,
    Hide,
    Swap,
}

internal enum PlayerVisibilityActionReason
{
    Maintain,
    TargetExpanded,
    TargetReduced,
    Swap,
}
