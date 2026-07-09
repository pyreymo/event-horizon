using System;
using System.Collections.Generic;
using EventHorizon.Culling.Rules;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerVisibilityReconciler
{
    private readonly List<PlayerVisibilityTarget> toShow = [];
    private readonly List<PlayerVisibilityTarget> toHide = [];
    private readonly List<PlayerVisibilityTarget> unchanged = [];
    private readonly List<PlayerVisibilityAction> actions = [];

    public PlayerVisibilityReconciliation Reconcile(PlayerVisibilityTargetSet targetSet, HiddenObjectTracker hiddenObjectTracker)
    {
        toShow.Clear();
        toHide.Clear();
        unchanged.Clear();
        actions.Clear();

        var appliedVisibleCount = 0;
        var desiredVisibleCount = 0;

        foreach (var target in targetSet.Targets)
        {
            var appliedVisible = !hiddenObjectTracker.IsHidden(target.Identity);
            if (appliedVisible)
            {
                appliedVisibleCount++;
            }

            if (target.DesiredVisible)
            {
                desiredVisibleCount++;
            }

            if (target.DesiredVisible && !appliedVisible)
            {
                toShow.Add(target);
            }
            else if (!target.DesiredVisible && appliedVisible)
            {
                toHide.Add(target);
            }
            else
            {
                unchanged.Add(target);
            }
        }

        toShow.Sort(CompareShowPriority);
        toHide.Sort(CompareHidePriority);

        AddTransitions(actions, toShow, toHide);
        AddMaintainedVisibility(actions, unchanged);

        return new PlayerVisibilityReconciliation(
            targetSet.Revision,
            actions,
            desiredVisibleCount,
            appliedVisibleCount,
            toShow.Count,
            toHide.Count
        );
    }

    private static void AddTransitions(
        List<PlayerVisibilityAction> actions,
        IReadOnlyList<PlayerVisibilityTarget> toShow,
        IReadOnlyList<PlayerVisibilityTarget> toHide
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

    private static void AddMaintainedVisibility(List<PlayerVisibilityAction> actions, IReadOnlyList<PlayerVisibilityTarget> unchanged)
    {
        foreach (var target in unchanged)
        {
            actions.Add(
                target.DesiredVisible
                    ? PlayerVisibilityAction.Show(target, PlayerVisibilityActionReason.Maintain)
                    : PlayerVisibilityAction.Hide(target, PlayerVisibilityActionReason.Maintain)
            );
        }
    }

    private static int CompareShowPriority(PlayerVisibilityTarget left, PlayerVisibilityTarget right)
    {
        var rankComparison = left.Decision.Rank.CompareTo(right.Decision.Rank);
        if (rankComparison != 0)
        {
            return rankComparison;
        }

        var tieBreakerComparison = PlayerKeepTieBreaker.Compare(left.Decision.TieBreaker, right.Decision.TieBreaker);
        if (tieBreakerComparison != 0)
        {
            return tieBreakerComparison;
        }

        var entityComparison = left.Identity.EntityId.CompareTo(right.Identity.EntityId);
        return entityComparison != 0 ? entityComparison : left.Identity.Address.ToInt64().CompareTo(right.Identity.Address.ToInt64());
    }

    private static int CompareHidePriority(PlayerVisibilityTarget left, PlayerVisibilityTarget right) => CompareShowPriority(right, left);
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
    PlayerVisibilityTarget Target,
    PlayerVisibilityTarget? PairedTarget
)
{
    public static PlayerVisibilityAction Show(PlayerVisibilityTarget target, PlayerVisibilityActionReason reason) =>
        new(PlayerVisibilityActionKind.Show, reason, target, null);

    public static PlayerVisibilityAction Hide(PlayerVisibilityTarget target, PlayerVisibilityActionReason reason) =>
        new(PlayerVisibilityActionKind.Hide, reason, target, null);

    public static PlayerVisibilityAction Swap(PlayerVisibilityTarget outgoing, PlayerVisibilityTarget incoming) =>
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
