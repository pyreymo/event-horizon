using System;
using System.Collections.Generic;
using System.Numerics;

namespace EventHorizon.Culling;

internal readonly record struct PlayerVisibilitySelectionCandidate(
    int SourceIndex,
    ulong GameObjectId,
    uint EntityId,
    int ObjectIndex,
    int Rank,
    Vector3 RelativePosition,
    Vector3 RelativeVelocity,
    bool WasPreviouslySelected
);

internal sealed record PlayerVisibilitySelectionSnapshot(
    int Budget,
    double SmoothedLocalSpeed,
    IReadOnlyList<PlayerVisibilitySelectionCandidate> Candidates
)
{
    public IReadOnlyList<PlayerVisibilitySelectionCandidate> Candidates { get; } =
        Candidates ?? throw new ArgumentNullException(nameof(Candidates));
}

internal sealed class PlayerVisibilitySelectionParameters
{
    public const int DefaultRankCount = 8;
    public const long DefaultRankStep = 3_000;
    public const long DefaultSoftScoreScale = 1_000;
    public const long DefaultRestRetentionBonus = 500;
    public const long DefaultMoveRetentionBonus = 23_000;
    public const int DefaultPredictionSteps = 4;
    public const double DefaultPredictionStepSeconds = 0.2;
    public const double DefaultPredictionGamma = 0.85;
    public const double DefaultDistanceSigma = 30.0;
    public const double DefaultMotionStartSpeed = 0.5;
    public const double DefaultMotionFullSpeed = 4.0;
    public const double DefaultLocalSpeedHalfLifeSeconds = 0.35;
    public const double DefaultMaxTrustedLocalSpeed = 50.0;

    public int RankCount => DefaultRankCount;
    public long RankStep => DefaultRankStep;
    public long SoftScoreScale => DefaultSoftScoreScale;
    public long RestRetentionBonus => DefaultRestRetentionBonus;
    public long MoveRetentionBonus => DefaultMoveRetentionBonus;
    public int PredictionSteps => DefaultPredictionSteps;
    public double PredictionStepSeconds => DefaultPredictionStepSeconds;
    public double PredictionGamma => DefaultPredictionGamma;
    public double DistanceSigma => DefaultDistanceSigma;
    public double MotionStartSpeed => DefaultMotionStartSpeed;
    public double MotionFullSpeed => DefaultMotionFullSpeed;
    public double LocalSpeedHalfLifeSeconds => DefaultLocalSpeedHalfLifeSeconds;
    public double MaxTrustedLocalSpeed => DefaultMaxTrustedLocalSpeed;
}

internal static class PlayerVisibilitySelector
{
    public static int[] Select(PlayerVisibilitySelectionSnapshot snapshot, PlayerVisibilitySelectionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);

        var candidates = CopyAndValidateCandidates(snapshot.Candidates, parameters.RankCount);
        var effectiveBudget = Math.Clamp(snapshot.Budget, 0, candidates.Length);
        var motionFactor = CalculateMotionFactor(snapshot.SmoothedLocalSpeed, parameters);
        var retentionBonus = CalculateRetentionBonus(motionFactor, parameters);
        var rankedCandidates = new ScoredCandidate[candidates.Length];

        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            var softScore = CalculateSoftScore(candidate.RelativePosition, candidate.RelativeVelocity, parameters);
            var softPoints = checked((long)Math.Round(parameters.SoftScoreScale * softScore, MidpointRounding.AwayFromZero));
            var priorityLevel = parameters.RankCount - 1 - candidate.Rank;
            var baseScore = checked((parameters.RankStep * priorityLevel) + softPoints);
            var adjustedScore = checked(baseScore + (candidate.WasPreviouslySelected ? retentionBonus : 0));
            rankedCandidates[i] = new ScoredCandidate(
                candidate.SourceIndex,
                candidate.GameObjectId,
                candidate.EntityId,
                candidate.ObjectIndex,
                baseScore,
                adjustedScore,
                candidate.WasPreviouslySelected
            );
        }

        Array.Sort(rankedCandidates, CompareScoredCandidates);

        var selectedSourceIndices = new int[effectiveBudget];
        for (var i = 0; i < effectiveBudget; i++)
        {
            var selected = rankedCandidates[i];
            selectedSourceIndices[i] = selected.SourceIndex;
        }

        return selectedSourceIndices;
    }

    internal static double CalculateSoftScore(
        Vector3 relativePosition,
        Vector3 relativeVelocity,
        PlayerVisibilitySelectionParameters parameters
    )
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!IsFinite(relativePosition))
        {
            return 0;
        }

        if (!IsFinite(relativeVelocity))
        {
            relativeVelocity = Vector3.Zero;
        }

        var weightedScore = 0.0;
        var totalWeight = 0.0;
        var weight = 1.0;
        for (var step = 0; step < parameters.PredictionSteps; step++)
        {
            var time = step * parameters.PredictionStepSeconds;
            var predictedPosition = relativePosition + (relativeVelocity * (float)time);
            if (!IsFinite(predictedPosition))
            {
                return 0;
            }

            var distance = predictedPosition.Length();
            if (!float.IsFinite(distance))
            {
                return 0;
            }

            var normalizedDistance = distance / parameters.DistanceSigma;
            var distanceScore = 1.0 / (1.0 + (normalizedDistance * normalizedDistance));
            weightedScore += weight * distanceScore;
            totalWeight += weight;
            weight *= parameters.PredictionGamma;
        }

        if (!double.IsFinite(weightedScore) || !double.IsFinite(totalWeight) || totalWeight <= 0)
        {
            return 0;
        }

        var softScore = weightedScore / totalWeight;
        return double.IsFinite(softScore) ? Math.Clamp(softScore, 0, 1) : 0;
    }

    private static PlayerVisibilitySelectionCandidate[] CopyAndValidateCandidates(
        IReadOnlyList<PlayerVisibilitySelectionCandidate> source,
        int rankCount
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rankCount);

        var candidates = new PlayerVisibilitySelectionCandidate[source.Count];
        var sourceIndices = new HashSet<int>();

        for (var i = 0; i < source.Count; i++)
        {
            var candidate = source[i];

            if (candidate.Rank < 0 || candidate.Rank >= rankCount)
            {
                throw new ArgumentException(
                    $"Candidate at index {i} has rank {candidate.Rank}; rank must be in [0, {rankCount - 1}].",
                    nameof(source)
                );
            }

            if (!sourceIndices.Add(candidate.SourceIndex))
            {
                throw new ArgumentException($"Candidate at index {i} has duplicate SourceIndex {candidate.SourceIndex}.", nameof(source));
            }

            candidates[i] = candidate;
        }

        return candidates;
    }

    private static double CalculateMotionFactor(double speed, PlayerVisibilitySelectionParameters parameters)
    {
        if (double.IsNaN(speed) || speed < 0)
        {
            speed = 0;
        }
        else if (double.IsPositiveInfinity(speed))
        {
            return 1;
        }

        var t = Math.Clamp((speed - parameters.MotionStartSpeed) / (parameters.MotionFullSpeed - parameters.MotionStartSpeed), 0, 1);
        return t * t * (3 - (2 * t));
    }

    private static long CalculateRetentionBonus(double motionFactor, PlayerVisibilitySelectionParameters parameters)
    {
        var bonus = parameters.RestRetentionBonus + ((parameters.MoveRetentionBonus - parameters.RestRetentionBonus) * motionFactor);
        return checked((long)Math.Round(bonus, MidpointRounding.AwayFromZero));
    }

    private static int CompareScoredCandidates(ScoredCandidate left, ScoredCandidate right)
    {
        var comparison = right.AdjustedScore.CompareTo(left.AdjustedScore);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = right.WasPreviouslySelected.CompareTo(left.WasPreviouslySelected);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = right.BaseScore.CompareTo(left.BaseScore);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.GameObjectId.CompareTo(right.GameObjectId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.EntityId.CompareTo(right.EntityId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.ObjectIndex.CompareTo(right.ObjectIndex);
        return comparison != 0 ? comparison : left.SourceIndex.CompareTo(right.SourceIndex);
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct ScoredCandidate(
        int SourceIndex,
        ulong GameObjectId,
        uint EntityId,
        int ObjectIndex,
        long BaseScore,
        long AdjustedScore,
        bool WasPreviouslySelected
    );
}

internal sealed class PlayerVisibilitySelectionController
{
    private static readonly Vector3 InvalidPosition = new(float.NaN, float.NaN, float.NaN);
    private readonly PlayerVisibilitySelectionParameters parameters;
    private readonly LocalSpeedSmoother localSpeedSmoother;
    private readonly PlayerVelocityTracker playerVelocityTracker;
    private readonly Action<Exception>? reportFailure;
    private HashSet<PlayerObjectIdentity> selectedHistory = [];
    private bool seeded;

    public PlayerVisibilitySelectionController(Action<Exception>? reportFailure = null)
    {
        parameters = new PlayerVisibilitySelectionParameters();
        localSpeedSmoother = new LocalSpeedSmoother(this.parameters);
        playerVelocityTracker = new PlayerVelocityTracker(this.parameters);
        this.reportFailure = reportFailure;
    }

    public PlayerVisibilitySelectionEvaluation Evaluate(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTarget[] legacyTargets,
        bool limitVisiblePlayerCount,
        int visiblePlayerCountLimit,
        Vector3? localPosition
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(legacyTargets);

        if (!localPosition.HasValue || !IsFinite(localPosition.Value))
        {
            return new PlayerVisibilitySelectionEvaluation(PlayerVisibilitySelectionStatus.Unavailable, []);
        }

        try
        {
            return EvaluateCore(plan, legacyTargets, limitVisiblePlayerCount, visiblePlayerCountLimit, localPosition.Value);
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

    public void CommitAppliedTarget(PlayerVisibilityTarget[] appliedTarget)
    {
        ArgumentNullException.ThrowIfNull(appliedTarget);
        var appliedSelection = new HashSet<PlayerObjectIdentity>();
        foreach (var target in appliedTarget)
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
        PlayerVisibilityTarget[] legacyTargets,
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

        var previousSelected = seeded ? [.. selectedHistory] : GetLegacySelectedIdentities(legacyTargets);
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

        var selectedKeys = new PlayerVisibilitySelectionKey[selection.Length];
        for (var selectedIndex = 0; selectedIndex < selection.Length; selectedIndex++)
        {
            var sourceIndex = selection[selectedIndex];
            var entry = plan.Entries[sourceIndex];
            selectedKeys[selectedIndex] = new(sourceIndex, entry.Identity, entry.ObjectIndex);
        }

        var status = localSpeedSmoother.HasVelocityEstimate
            ? PlayerVisibilitySelectionStatus.Ready
            : PlayerVisibilitySelectionStatus.Warmup;
        return new PlayerVisibilitySelectionEvaluation(status, Array.AsReadOnly(selectedKeys));
    }

    private static HashSet<PlayerObjectIdentity> GetLegacySelectedIdentities(PlayerVisibilityTarget[] targets)
    {
        var identities = new HashSet<PlayerObjectIdentity>();
        foreach (var target in targets)
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
