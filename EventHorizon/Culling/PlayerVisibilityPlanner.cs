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
        var resolution = PlayerVisibilityActiveTargetResolver.Resolve(plan, legacyTarget, evaluation, stableTargetBuffer);
        if (resolution.FailureException != null)
        {
            reportFailure(resolution.FailureException);
        }
        var budgetStats = PlayerVisibilityActiveBudgetStats.Calculate(resolution.ActiveTarget, visiblePlayerCountLimit);
        var reconciliation = reconciler.Reconcile(resolution.ActiveTarget, hiddenObjectTracker);
        return new PlayerVisibilityFrameState(resolution.ActiveTarget, reconciliation, budgetStats);
    }

    public void Commit(PlayerVisibilityFrameState frame) => selectionController.CommitAppliedTarget(frame.ActiveTarget);

    public void Reset() => selectionController.Reset();

    public unsafe PlayerVisibilityPlan BuildPlan(GameObjectManager* manager, PlayerKeepPlan keepPlan, uint? previewVisibleEntityId) =>
        PlayerVisibilityPlan.Build(manager, keepPlan, previewVisibleEntityId, planEntryBuffer);

    public PlayerVisibilityTargetSet BuildLegacyTarget(PlayerVisibilityPlan plan) =>
        PlayerVisibilityLegacyTargetBuilder.Build(plan, legacyTargetBuffer);
}

internal sealed unsafe class PlayerVisibilityPlan
{
    internal PlayerVisibilityPlan(TimeSpan sampleTime, IReadOnlyList<PlayerVisibilityPlanEntry> entries)
    {
        SampleTime = sampleTime;
        Entries = entries;
    }

    public TimeSpan SampleTime { get; }
    public IReadOnlyList<PlayerVisibilityPlanEntry> Entries { get; }

    public static PlayerVisibilityPlan Build(
        GameObjectManager* manager,
        PlayerKeepPlan keepPlan,
        uint? previewVisibleEntityId,
        List<PlayerVisibilityPlanEntry> entries
    )
    {
        entries.Clear();
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
            var cutByBudget = keepPlan.IsCutByBudget(address);
            var hasPosition = TryGetPosition(gameObject, out var position);

            var entry = new PlayerVisibilityPlanEntry(
                PlayerObjectIdentity.From(gameObject),
                index,
                classification,
                keepDecision,
                cutByBudget,
                position,
                hasPosition
            );
            entries.Add(entry);
        }

        return new PlayerVisibilityPlan(TimeSpan.FromMilliseconds(Environment.TickCount64), [.. entries]);
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

    private static bool TryGetPosition(GameObject* gameObject, out Vector3 position)
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

internal readonly record struct PlayerVisibilityPlanEntry(
    PlayerObjectIdentity Identity,
    int ObjectIndex,
    PlayerVisibilityClassification Classification,
    PlayerKeepDecision Decision,
    bool CutByBudget,
    Vector3 Position,
    bool HasPosition
)
{
    public bool IsManaged => Classification != PlayerVisibilityClassification.Unmanaged;
}

internal sealed class PlayerVisibilityTargetSet(IReadOnlyList<PlayerVisibilityTarget> targets)
{
    public IReadOnlyList<PlayerVisibilityTarget> Targets { get; } = targets;
}

file static class PlayerVisibilityLegacyTargetBuilder
{
    public static PlayerVisibilityTargetSet Build(PlayerVisibilityPlan plan, List<PlayerVisibilityTarget> targets)
    {
        targets.Clear();
        foreach (var entry in plan.Entries)
        {
            if (!entry.IsManaged)
            {
                continue;
            }

            targets.Add(
                new PlayerVisibilityTarget(
                    entry.Identity,
                    entry.ObjectIndex,
                    entry.Classification,
                    GetDesiredVisible(entry),
                    entry.Decision,
                    entry.CutByBudget
                )
            );
        }

        return new PlayerVisibilityTargetSet([.. targets]);
    }

    private static bool GetDesiredVisible(PlayerVisibilityPlanEntry entry) =>
        entry.Classification switch
        {
            PlayerVisibilityClassification.BypassVisible => true,
            PlayerVisibilityClassification.Competitive => !entry.CutByBudget,
            PlayerVisibilityClassification.ForceHidden => false,
            PlayerVisibilityClassification.Unmanaged => true,
            _ => true,
        };
}

internal readonly record struct PlayerVisibilityTarget(
    PlayerObjectIdentity Identity,
    int ObjectIndex,
    PlayerVisibilityClassification Classification,
    bool DesiredVisible,
    PlayerKeepDecision Decision,
    bool CutByBudget
);

internal enum PlayerVisibilityClassification
{
    BypassVisible,
    Competitive,
    ForceHidden,
    Unmanaged,
}

internal readonly record struct PlayerObjectIdentity(nint Address, ulong GameObjectId, uint EntityId)
{
    public static unsafe PlayerObjectIdentity From(GameObject* gameObject) =>
        new((nint)gameObject, (ulong)gameObject->GetGameObjectId(), gameObject->EntityId);

    public unsafe bool Matches(GameObject* gameObject) =>
        gameObject != null
        && (nint)gameObject == Address
        && (ulong)gameObject->GetGameObjectId() == GameObjectId
        && gameObject->EntityId == EntityId;
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

file static class PlayerVisibilityStableTargetBuilder
{
    public static PlayerVisibilityTargetSet Build(
        PlayerVisibilityPlan plan,
        IReadOnlyCollection<PlayerVisibilitySelectionKey> selectedCompetitiveKeys,
        List<PlayerVisibilityTarget> targets
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selectedCompetitiveKeys);
        ArgumentNullException.ThrowIfNull(targets);

        var competitiveSourceIndices = new HashSet<int>();
        for (var sourceIndex = 0; sourceIndex < plan.Entries.Count; sourceIndex++)
        {
            var entry = plan.Entries[sourceIndex];
            if (entry.Classification == PlayerVisibilityClassification.Competitive)
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

        targets.Clear();
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
            targets.Add(
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

        return new PlayerVisibilityTargetSet([.. targets]);
    }
}

file static class PlayerVisibilityActiveBudgetStats
{
    public static PlayerKeepBudgetStats Calculate(PlayerVisibilityTargetSet activeTarget, int visiblePlayerCountLimit)
    {
        ArgumentNullException.ThrowIfNull(activeTarget);
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

        return new PlayerKeepBudgetStats(bypassVisibleCount, visibleCompetitiveCount, Math.Clamp(visiblePlayerCountLimit, 1, 100));
    }
}

file sealed record PlayerVisibilityActiveTargetResolution(PlayerVisibilityTargetSet ActiveTarget, Exception? FailureException = null);

file static class PlayerVisibilityActiveTargetResolver
{
    public static PlayerVisibilityActiveTargetResolution Resolve(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet legacyTarget,
        PlayerVisibilitySelectionEvaluation evaluation,
        List<PlayerVisibilityTarget> stableTargetBuffer
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(legacyTarget);
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(stableTargetBuffer);

        var activeTarget = legacyTarget;
        Exception? failureException = null;
        if (evaluation.Status == PlayerVisibilitySelectionStatus.Ready)
        {
            try
            {
                activeTarget = PlayerVisibilityStableTargetBuilder.Build(plan, evaluation.SelectedKeys, stableTargetBuffer);
            }
            catch (Exception exception)
            {
                failureException = exception;
                activeTarget = legacyTarget;
            }
        }
        return new PlayerVisibilityActiveTargetResolution(activeTarget, failureException);
    }
}
