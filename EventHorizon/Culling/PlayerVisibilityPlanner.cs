using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed class PlayerVisibilityPlanner(Action<Exception> reportSelectionFailure)
{
    private readonly PlayerVisibilitySelectionController selectionController = new(reportFailure: reportSelectionFailure);
    private readonly PlayerVisibilityReconciler reconciler = new();
    private readonly Action<Exception> reportFailure = reportSelectionFailure;
    private readonly List<PlayerVisibilityPlanEntry> planEntryBuffer = [];
    private readonly List<PlayerVisibilityTarget> legacyTargetBuffer = [];
    private readonly List<PlayerVisibilityTarget> stableTargetBuffer = [];

    public PlayerVisibilityFrameState BuildFrame(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet legacyTarget,
        bool limitVisiblePlayerCount,
        int visiblePlayerCountLimit,
        Vector3? localPosition,
        HiddenObjectTracker hiddenObjectTracker
    )
    {
        var evaluation = selectionController.Evaluate(plan, legacyTarget, limitVisiblePlayerCount, visiblePlayerCountLimit, localPosition);
        var resolution = ResolveActiveTarget(plan, legacyTarget, evaluation);
        if (resolution.FailureException != null)
        {
            reportFailure(resolution.FailureException);
        }
        var budgetStats = CalculateBudgetStats(resolution.ActiveTarget, visiblePlayerCountLimit);
        var reconciliation = reconciler.Reconcile(resolution.ActiveTarget, hiddenObjectTracker);
        return new PlayerVisibilityFrameState(resolution.ActiveTarget, reconciliation, budgetStats);
    }

    public void Commit(PlayerVisibilityFrameState frame) => selectionController.CommitAppliedTarget(frame.ActiveTarget);

    public void Reset() => selectionController.Reset();

    public unsafe PlayerVisibilityPlan BuildPlan(GameObjectManager* manager, PlayerKeepPlan keepPlan, uint? previewVisibleEntityId)
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

    public PlayerVisibilityTargetSet BuildLegacyTarget(PlayerVisibilityPlan plan)
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
                        entry.CutByBudget
                    )
                );
            }
        }

        return new PlayerVisibilityTargetSet([.. legacyTargetBuffer]);
    }

    private ActiveTargetResolution ResolveActiveTarget(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet legacyTarget,
        PlayerVisibilitySelectionEvaluation evaluation
    )
    {
        if (evaluation.Status != PlayerVisibilitySelectionStatus.Ready)
        {
            return new(legacyTarget);
        }

        try
        {
            return new(BuildStableTarget(plan, evaluation.SelectedKeys));
        }
        catch (Exception exception)
        {
            return new(legacyTarget, exception);
        }
    }

    private PlayerVisibilityTargetSet BuildStableTarget(
        PlayerVisibilityPlan plan,
        IReadOnlyCollection<PlayerVisibilitySelectionKey> selectedCompetitiveKeys
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
                    entry.Classification == PlayerVisibilityClassification.Competitive && !desiredVisible
                )
            );
        }

        return new PlayerVisibilityTargetSet([.. stableTargetBuffer]);
    }

    private static PlayerKeepBudgetStats CalculateBudgetStats(PlayerVisibilityTargetSet activeTarget, int visiblePlayerCountLimit)
    {
        var bypassVisibleCount = 0;
        var visibleCompetitiveCount = 0;
        foreach (var target in activeTarget.Targets)
        {
            if (target.Classification == PlayerVisibilityClassification.BypassVisible && target.DesiredVisible)
            {
                bypassVisibleCount++;
            }
            else if (target.Classification == PlayerVisibilityClassification.Competitive && target.DesiredVisible)
            {
                visibleCompetitiveCount++;
            }
        }

        return new(bypassVisibleCount, visibleCompetitiveCount, Math.Clamp(visiblePlayerCountLimit, 1, 100));
    }

    private static PlayerVisibilityClassification Classify(int index, PlayerKeepDecision keepDecision, bool previewVisible)
    {
        if (!PlayerObjectSlots.IsPlayer(index))
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

    private sealed record ActiveTargetResolution(PlayerVisibilityTargetSet ActiveTarget, Exception? FailureException = null);
}

internal sealed class PlayerVisibilitySelectionController
{
    private static readonly Vector3 InvalidPosition = new(float.NaN, float.NaN, float.NaN);
    private readonly PlayerVisibilitySelectionParameters parameters;
    private readonly LocalSpeedSmoother localSpeedSmoother;
    private readonly PlayerVelocityTracker<PlayerObjectIdentity> playerVelocityTracker;
    private readonly Action<Exception>? reportFailure;
    private HashSet<PlayerObjectIdentity> selectedHistory = [];
    private bool seeded;

    public PlayerVisibilitySelectionController(
        PlayerVisibilitySelectionParameters? parameters = null,
        Action<Exception>? reportFailure = null
    )
    {
        this.parameters = parameters ?? PlayerVisibilitySelectionParameters.Default;
        localSpeedSmoother = new LocalSpeedSmoother(this.parameters);
        playerVelocityTracker = new PlayerVelocityTracker<PlayerObjectIdentity>(this.parameters);
        this.reportFailure = reportFailure;
    }

    public PlayerVisibilitySelectionEvaluation Evaluate(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet legacyTargetSet,
        bool limitVisiblePlayerCount,
        int visiblePlayerCountLimit,
        Vector3? localPosition
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(legacyTargetSet);

        if (!localPosition.HasValue || !IsFinite(localPosition.Value))
        {
            return new PlayerVisibilitySelectionEvaluation(PlayerVisibilitySelectionStatus.Unavailable, []);
        }

        try
        {
            return EvaluateCore(plan, legacyTargetSet, limitVisiblePlayerCount, visiblePlayerCountLimit, localPosition.Value);
        }
        catch (Exception exception)
        {
            reportFailure?.Invoke(exception);
            return new PlayerVisibilitySelectionEvaluation(PlayerVisibilitySelectionStatus.Failed, []);
        }
    }

    public void Reset()
    {
        seeded = false;
        selectedHistory = [];
        localSpeedSmoother.Reset();
        playerVelocityTracker.Clear();
    }

    public void CommitAppliedTarget(PlayerVisibilityTargetSet appliedTarget)
    {
        ArgumentNullException.ThrowIfNull(appliedTarget);
        var appliedSelection = new HashSet<PlayerObjectIdentity>();
        foreach (var target in appliedTarget.Targets)
        {
            if (target.Classification == PlayerVisibilityClassification.Competitive && target.DesiredVisible)
            {
                appliedSelection.Add(target.Identity);
            }
        }

        selectedHistory = appliedSelection;
        seeded = true;
    }

    private PlayerVisibilitySelectionEvaluation EvaluateCore(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet legacyTargetSet,
        bool limitVisiblePlayerCount,
        int visiblePlayerCountLimit,
        Vector3 localPosition
    )
    {
        var timestamp = plan.SampleTime;
        localSpeedSmoother.AddSample(timestamp, localPosition);

        var competitiveIdentities = new HashSet<PlayerObjectIdentity>();
        var velocityEstimates = new Dictionary<PlayerObjectIdentity, PlayerVelocityEstimate>();
        var competitiveCount = 0;
        for (var sourceIndex = 0; sourceIndex < plan.Entries.Count; sourceIndex++)
        {
            var entry = plan.Entries[sourceIndex];
            if (entry.Classification != PlayerVisibilityClassification.Competitive)
            {
                continue;
            }

            competitiveCount++;
            competitiveIdentities.Add(entry.Identity);
            velocityEstimates[entry.Identity] = entry.HasPosition
                ? playerVelocityTracker.AddSample(timestamp, entry.Identity, entry.Position)
                : playerVelocityTracker.GetEstimate(entry.Identity);
        }

        playerVelocityTracker.PruneExcept(competitiveIdentities);

        var previousSelected = seeded ? [.. selectedHistory] : GetLegacySelectedIdentities(legacyTargetSet);
        var candidates = new List<PlayerVisibilitySelectionCandidate>(competitiveCount);
        for (var sourceIndex = 0; sourceIndex < plan.Entries.Count; sourceIndex++)
        {
            var entry = plan.Entries[sourceIndex];
            if (entry.Classification != PlayerVisibilityClassification.Competitive)
            {
                continue;
            }

            var otherVelocityEstimate = velocityEstimates[entry.Identity];
            var relativeVelocity = CalculateRelativeVelocity(
                otherVelocityEstimate.Velocity,
                otherVelocityEstimate.HasVelocityEstimate,
                localSpeedSmoother.SmoothedVelocity,
                localSpeedSmoother.HasVelocityEstimate
            );
            candidates.Add(
                new PlayerVisibilitySelectionCandidate(
                    sourceIndex,
                    entry.Identity.GameObjectId,
                    entry.Identity.EntityId,
                    entry.ObjectIndex,
                    entry.Decision.Rank,
                    entry.HasPosition ? entry.Position - localPosition : InvalidPosition,
                    relativeVelocity,
                    previousSelected.Contains(entry.Identity)
                )
            );
        }

        var budget = limitVisiblePlayerCount ? Math.Clamp(visiblePlayerCountLimit, 1, 100) : candidates.Count;
        var snapshot = new PlayerVisibilitySelectionSnapshot(budget, localSpeedSmoother.SmoothedSpeed, candidates);
        var selection = PlayerVisibilitySelector.Select(snapshot, parameters);

        var selectedKeys = new PlayerVisibilitySelectionKey[selection.SelectedSourceIndices.Count];
        for (var selectedIndex = 0; selectedIndex < selection.SelectedSourceIndices.Count; selectedIndex++)
        {
            var sourceIndex = selection.SelectedSourceIndices[selectedIndex];
            var entry = plan.Entries[sourceIndex];
            selectedKeys[selectedIndex] = new(sourceIndex, entry.Identity, entry.ObjectIndex);
        }

        var status = localSpeedSmoother.HasVelocityEstimate
            ? PlayerVisibilitySelectionStatus.Ready
            : PlayerVisibilitySelectionStatus.Warmup;
        return new PlayerVisibilitySelectionEvaluation(status, Array.AsReadOnly(selectedKeys));
    }

    private static HashSet<PlayerObjectIdentity> GetLegacySelectedIdentities(PlayerVisibilityTargetSet targetSet)
    {
        var identities = new HashSet<PlayerObjectIdentity>();
        foreach (var target in targetSet.Targets)
        {
            if (target.Classification == PlayerVisibilityClassification.Competitive && target.DesiredVisible)
            {
                identities.Add(target.Identity);
            }
        }

        return identities;
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    internal static Vector3 CalculateRelativeVelocity(
        Vector3 otherVelocity,
        bool hasOtherVelocity,
        Vector3 localVelocity,
        bool hasLocalVelocity
    ) => (hasOtherVelocity ? otherVelocity : Vector3.Zero) - (hasLocalVelocity ? localVelocity : Vector3.Zero);
}

internal sealed record PlayerVisibilitySelectionEvaluation(
    PlayerVisibilitySelectionStatus Status,
    IReadOnlyList<PlayerVisibilitySelectionKey> SelectedKeys
);

internal readonly record struct PlayerVisibilitySelectionKey(int SourceIndex, PlayerObjectIdentity Identity, int ObjectIndex);

internal enum PlayerVisibilitySelectionStatus
{
    Ready,
    Warmup,
    Unavailable,
    Failed,
}
