using System;
using System.Collections.Generic;
using System.Numerics;
using EventHorizon.Culling.Selection;

namespace EventHorizon.Culling.Visibility;

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
        var timestamp = TimeSpan.FromMilliseconds(plan.CreatedAtTickCount64);
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
