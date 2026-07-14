using System;
using System.Collections.Generic;
using System.Numerics;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed class PlayerVisibilityPlanner(Action<Exception> reportSelectionFailure)
{
    private readonly PlayerVisibilitySelectionController selectionController = new(reportFailure: reportSelectionFailure);
    private readonly Action<Exception> reportFailure = reportSelectionFailure;
    private readonly List<PlayerVisibilityPlanEntry> planEntryBuffer = [];
    private readonly List<PlayerVisibilityTarget> legacyTargetBuffer = [];
    private readonly List<PlayerVisibilityTarget> stableTargetBuffer = [];
    private readonly List<PlayerVisibilityTarget> targetsToShow = [];
    private readonly List<PlayerVisibilityTarget> targetsToHide = [];
    private readonly List<PlayerVisibilityAction> reconciliationActions = [];

    public unsafe PlayerVisibilityFrameState BuildFrame(
        GameObjectManager* manager,
        PlayerKeepPlan keepPlan,
        uint? previewVisibleEntityId,
        bool limitVisiblePlayerCount,
        int visiblePlayerCountLimit,
        Vector3? localPosition,
        float softScoreHalfDistance,
        HiddenObjectTracker hiddenObjectTracker
    )
    {
        var plan = BuildPlan(manager, keepPlan, previewVisibleEntityId);
        var legacyTarget = BuildLegacyTarget(plan);
        var evaluation = selectionController.Evaluate(
            plan,
            legacyTarget,
            limitVisiblePlayerCount,
            visiblePlayerCountLimit,
            localPosition,
            softScoreHalfDistance
        );
        var activeTarget = legacyTarget;
        if (evaluation.Status == PlayerVisibilitySelectionStatus.Ready)
        {
            try
            {
                activeTarget = BuildStableTarget(plan, evaluation.SelectedKeys, evaluation.Scores);
            }
            catch (Exception exception)
            {
                reportFailure(exception);
            }
        }
        else
        {
            activeTarget = AddSelectionScores(plan, legacyTarget, evaluation.Scores);
        }

        var actions = Reconcile(activeTarget, hiddenObjectTracker);
        return new PlayerVisibilityFrameState(activeTarget, actions);
    }

    public void Commit(PlayerVisibilityFrameState frame) => selectionController.CommitAppliedTarget(frame.ActiveTarget);

    public void Reset() => selectionController.Reset();

    private unsafe PlayerVisibilityPlan BuildPlan(GameObjectManager* manager, PlayerKeepPlan keepPlan, uint? previewVisibleEntityId)
    {
        planEntryBuffer.Clear();
        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject == null || gameObject->ObjectKind != ObjectKind.Pc)
            {
                continue;
            }

            var address = (nint)gameObject;
            var keepDecision = keepPlan.GetDecision(address);
            var classification = Classify(index, keepDecision, previewVisibleEntityId == gameObject->EntityId);
            var hasPosition = TryGetPosition(gameObject, out var position);
            planEntryBuffer.Add(
                new PlayerVisibilityPlanEntry(
                    PlayerObjectIdentity.From(gameObject),
                    index,
                    classification,
                    keepDecision,
                    keepPlan.IsCutByBudget(address),
                    position,
                    hasPosition
                )
            );
        }

        return new PlayerVisibilityPlan(TimeSpan.FromMilliseconds(Environment.TickCount64), [.. planEntryBuffer]);
    }

    private PlayerVisibilityTarget[] BuildLegacyTarget(PlayerVisibilityPlan plan)
    {
        legacyTargetBuffer.Clear();
        foreach (var entry in plan.Entries)
        {
            if (entry.IsManaged)
            {
                legacyTargetBuffer.Add(
                    new PlayerVisibilityTarget(
                        entry.Identity,
                        entry.ObjectIndex,
                        entry.Classification,
                        GetLegacyDesiredVisible(entry),
                        entry.Decision,
                        entry.CutByBudget,
                        null
                    )
                );
            }
        }

        return [.. legacyTargetBuffer];
    }

    private PlayerVisibilityTarget[] BuildStableTarget(
        PlayerVisibilityPlan plan,
        IReadOnlyCollection<PlayerVisibilitySelectionKey> selectedCompetitiveKeys,
        IReadOnlyCollection<PlayerVisibilitySelectionScore> selectionScores
    )
    {
        var competitiveSourceIndices = new HashSet<int>();
        for (var sourceIndex = 0; sourceIndex < plan.Entries.Count; sourceIndex++)
        {
            if (plan.Entries[sourceIndex].Classification == PlayerVisibilityClassification.Competitive)
            {
                competitiveSourceIndices.Add(sourceIndex);
            }
        }

        var selected = new HashSet<int>();
        foreach (var key in selectedCompetitiveKeys)
        {
            if (
                !competitiveSourceIndices.Contains(key.SourceIndex)
                || plan.Entries[key.SourceIndex].Identity != key.Identity
                || plan.Entries[key.SourceIndex].ObjectIndex != key.ObjectIndex
            )
            {
                throw new ArgumentException(
                    "Selected key does not map to the same Competitive entry in the current player visibility plan.",
                    nameof(selectedCompetitiveKeys)
                );
            }
            selected.Add(key.SourceIndex);
        }

        var scoresBySourceIndex = new Dictionary<int, PlayerVisibilitySelectionScore>();
        foreach (var score in selectionScores)
        {
            if (!competitiveSourceIndices.Contains(score.SourceIndex) || !scoresBySourceIndex.TryAdd(score.SourceIndex, score))
            {
                throw new ArgumentException(
                    "Selection score does not map uniquely to a Competitive entry in the current player visibility plan.",
                    nameof(selectionScores)
                );
            }
        }

        stableTargetBuffer.Clear();
        for (var sourceIndex = 0; sourceIndex < plan.Entries.Count; sourceIndex++)
        {
            var entry = plan.Entries[sourceIndex];
            if (!entry.IsManaged)
            {
                continue;
            }

            var desiredVisible = entry.Classification switch
            {
                PlayerVisibilityClassification.BypassVisible => true,
                PlayerVisibilityClassification.Competitive => selected.Contains(sourceIndex),
                PlayerVisibilityClassification.ForceHidden => false,
                _ => throw new InvalidOperationException($"Unsupported player visibility classification: {entry.Classification}."),
            };
            stableTargetBuffer.Add(
                new PlayerVisibilityTarget(
                    entry.Identity,
                    entry.ObjectIndex,
                    entry.Classification,
                    desiredVisible,
                    entry.Decision,
                    entry.Classification == PlayerVisibilityClassification.Competitive && !desiredVisible,
                    scoresBySourceIndex.TryGetValue(sourceIndex, out var score) ? score : null
                )
            );
        }

        return [.. stableTargetBuffer];
    }

    private static PlayerVisibilityTarget[] AddSelectionScores(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTarget[] targets,
        IReadOnlyCollection<PlayerVisibilitySelectionScore> selectionScores
    )
    {
        if (selectionScores.Count == 0)
        {
            return targets;
        }

        var scoresByIdentity = new Dictionary<PlayerObjectIdentity, PlayerVisibilitySelectionScore>();
        foreach (var score in selectionScores)
        {
            if ((uint)score.SourceIndex >= (uint)plan.Entries.Count)
            {
                throw new ArgumentException(
                    "Selection score source index is outside the current player visibility plan.",
                    nameof(selectionScores)
                );
            }

            var entry = plan.Entries[score.SourceIndex];
            if (entry.Classification != PlayerVisibilityClassification.Competitive || !scoresByIdentity.TryAdd(entry.Identity, score))
            {
                throw new ArgumentException(
                    "Selection score does not map uniquely to a Competitive entry in the current player visibility plan.",
                    nameof(selectionScores)
                );
            }
        }

        var scoredTargets = new PlayerVisibilityTarget[targets.Length];
        for (var index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            scoredTargets[index] = target with
            {
                SelectionScore = scoresByIdentity.TryGetValue(target.Identity, out var score) ? score : null,
            };
        }

        return scoredTargets;
    }

    private PlayerVisibilityAction[] Reconcile(PlayerVisibilityTarget[] targets, HiddenObjectTracker hiddenObjectTracker)
    {
        targetsToShow.Clear();
        targetsToHide.Clear();
        reconciliationActions.Clear();

        foreach (var target in targets)
        {
            var appliedVisible = !hiddenObjectTracker.IsHidden(target.Identity);
            if (target.DesiredVisible && !appliedVisible)
            {
                targetsToShow.Add(target);
            }
            else if (!target.DesiredVisible && appliedVisible)
            {
                targetsToHide.Add(target);
            }
        }

        targetsToShow.Sort(CompareShowPriority);
        targetsToHide.Sort(CompareHidePriority);
        AddTransitions(reconciliationActions, targetsToShow, targetsToHide);
        return [.. reconciliationActions];
    }

    private static void AddTransitions(
        List<PlayerVisibilityAction> actions,
        List<PlayerVisibilityTarget> toShow,
        List<PlayerVisibilityTarget> toHide
    )
    {
        var swapCount = Math.Min(toShow.Count, toHide.Count);
        for (var index = 0; index < swapCount; index++)
        {
            actions.Add(PlayerVisibilityAction.Swap(toHide[index], toShow[index]));
        }

        for (var index = swapCount; index < toHide.Count; index++)
        {
            actions.Add(PlayerVisibilityAction.Hide(toHide[index]));
        }

        for (var index = swapCount; index < toShow.Count; index++)
        {
            actions.Add(PlayerVisibilityAction.Show(toShow[index]));
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

    private static PlayerVisibilityClassification Classify(int index, PlayerKeepDecision keepDecision, bool previewVisible)
    {
        if (!CharacterObjectSlots.IsEvenSlot(index))
        {
            return PlayerVisibilityClassification.Unmanaged;
        }

        if (previewVisible)
        {
            return PlayerVisibilityClassification.BypassVisible;
        }

        return keepDecision.Kind switch
        {
            PlayerKeepDecisionKind.Keep when keepDecision.BudgetPolicy == PlayerKeepBudgetPolicy.Exempt =>
                PlayerVisibilityClassification.BypassVisible,
            PlayerKeepDecisionKind.Keep when keepDecision.BudgetPolicy == PlayerKeepBudgetPolicy.Counted =>
                PlayerVisibilityClassification.Competitive,
            _ => PlayerVisibilityClassification.ForceHidden,
        };
    }

    private static bool GetLegacyDesiredVisible(PlayerVisibilityPlanEntry entry) =>
        entry.Classification switch
        {
            PlayerVisibilityClassification.BypassVisible => true,
            PlayerVisibilityClassification.Competitive => !entry.CutByBudget,
            PlayerVisibilityClassification.ForceHidden => false,
            PlayerVisibilityClassification.Unmanaged => true,
            _ => true,
        };

    private static unsafe bool TryGetPosition(GameObject* gameObject, out Vector3 position)
    {
        position = default;
        if (gameObject == null || gameObject->VirtualTable == null)
        {
            return false;
        }

        var positionPtr = gameObject->GetPosition();
        if (positionPtr == null)
        {
            return false;
        }

        position = (Vector3)(*positionPtr);
        return true;
    }
}
