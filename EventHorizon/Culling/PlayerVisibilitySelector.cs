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
    public const long DefaultRestRetentionBonus = 200;
    public const long DefaultMoveRetentionBonus = 6_000;
    public const int DefaultPredictionSteps = 4;
    public const double DefaultPredictionStepSeconds = 0.2;
    public const double DefaultPredictionGamma = 0.85;
    public const double DefaultDistanceSigma = 30.0;
    public const double DefaultMotionStartSpeed = 0.5;
    public const double DefaultMotionFullSpeed = 4.0;
    public const double DefaultLocalSpeedHalfLifeSeconds = 0.20;
    public const double DefaultMaxTrustedLocalSpeed = 50.0;

    public static int RankCount => DefaultRankCount;
    public static long RankStep => DefaultRankStep;
    public static long SoftScoreScale => DefaultSoftScoreScale;
    public static long RestRetentionBonus => DefaultRestRetentionBonus;
    public static long MoveRetentionBonus => DefaultMoveRetentionBonus;
    public static int PredictionSteps => DefaultPredictionSteps;
    public static double PredictionStepSeconds => DefaultPredictionStepSeconds;
    public static double PredictionGamma => DefaultPredictionGamma;
    public double DistanceSigma { get; set; } = DefaultDistanceSigma;
    public static double MotionStartSpeed => DefaultMotionStartSpeed;
    public static double MotionFullSpeed => DefaultMotionFullSpeed;
    public static double LocalSpeedHalfLifeSeconds => DefaultLocalSpeedHalfLifeSeconds;
    public static double MaxTrustedLocalSpeed => DefaultMaxTrustedLocalSpeed;
}

internal static class PlayerVisibilitySelector
{
    public static int[] Select(PlayerVisibilitySelectionSnapshot snapshot, PlayerVisibilitySelectionParameters parameters) =>
        Evaluate(snapshot, parameters).SelectedSourceIndices;

    public static PlayerVisibilitySelectionResult Evaluate(
        PlayerVisibilitySelectionSnapshot snapshot,
        PlayerVisibilitySelectionParameters parameters
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!double.IsFinite(parameters.DistanceSigma) || parameters.DistanceSigma <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "DistanceSigma must be finite and greater than zero.");
        }

        var candidates = CopyAndValidateCandidates(snapshot.Candidates, PlayerVisibilitySelectionParameters.RankCount);
        var effectiveBudget = Math.Clamp(snapshot.Budget, 0, candidates.Length);
        var motionFactor = CalculateMotionFactor(snapshot.SmoothedLocalSpeed, parameters);
        var retentionBonus = CalculateRetentionBonus(motionFactor, parameters);
        var rankedCandidates = new ScoredCandidate[candidates.Length];

        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            var softScoreTrace = CalculateSoftScoreTrace(candidate.RelativePosition, candidate.RelativeVelocity, parameters);
            var softPoints = checked(
                (long)Math.Round(PlayerVisibilitySelectionParameters.SoftScoreScale * softScoreTrace.Score, MidpointRounding.AwayFromZero)
            );
            var priorityLevel = PlayerVisibilitySelectionParameters.RankCount - 1 - candidate.Rank;
            var rankPoints = checked(PlayerVisibilitySelectionParameters.RankStep * priorityLevel);
            var baseScore = checked(rankPoints + softPoints);
            var appliedRetentionBonus = candidate.WasPreviouslySelected ? retentionBonus : 0;
            var adjustedScore = checked(baseScore + appliedRetentionBonus);
            rankedCandidates[i] = new ScoredCandidate(
                candidate.SourceIndex,
                candidate.GameObjectId,
                candidate.EntityId,
                candidate.ObjectIndex,
                softScoreTrace.Score,
                softScoreTrace.PredictedDistances,
                softPoints,
                rankPoints,
                appliedRetentionBonus,
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

        var scores = new PlayerVisibilitySelectionScore[rankedCandidates.Length];
        for (var i = 0; i < rankedCandidates.Length; i++)
        {
            var candidate = rankedCandidates[i];
            scores[i] = new PlayerVisibilitySelectionScore(
                candidate.SourceIndex,
                candidate.SoftScore,
                candidate.PredictedDistances,
                parameters.DistanceSigma,
                candidate.SoftPoints,
                candidate.RankPoints,
                candidate.RetentionPoints,
                candidate.AdjustedScore
            );
        }

        return new PlayerVisibilitySelectionResult(selectedSourceIndices, scores);
    }

    internal static double CalculateSoftScore(
        Vector3 relativePosition,
        Vector3 relativeVelocity,
        PlayerVisibilitySelectionParameters parameters
    ) => CalculateSoftScoreTrace(relativePosition, relativeVelocity, parameters).Score;

    private static SoftScoreTrace CalculateSoftScoreTrace(
        Vector3 relativePosition,
        Vector3 relativeVelocity,
        PlayerVisibilitySelectionParameters parameters
    )
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!IsFinite(relativePosition))
        {
            return SoftScoreTrace.Zero;
        }

        if (!IsFinite(relativeVelocity))
        {
            relativeVelocity = Vector3.Zero;
        }

        var weightedScore = 0.0;
        var totalWeight = 0.0;
        var weight = 1.0;
        var predictedDistances = Vector4.Zero;
        for (var step = 0; step < PlayerVisibilitySelectionParameters.PredictionSteps; step++)
        {
            var time = step * PlayerVisibilitySelectionParameters.PredictionStepSeconds;
            var predictedPosition = relativePosition + (relativeVelocity * (float)time);
            if (!IsFinite(predictedPosition))
            {
                return SoftScoreTrace.Zero;
            }

            var distance = predictedPosition.Length();
            if (!float.IsFinite(distance))
            {
                return SoftScoreTrace.Zero;
            }

            predictedDistances[step] = distance;

            var normalizedDistance = distance / parameters.DistanceSigma;
            var distanceScore = 1.0 / (1.0 + (normalizedDistance * normalizedDistance));
            weightedScore += weight * distanceScore;
            totalWeight += weight;
            weight *= PlayerVisibilitySelectionParameters.PredictionGamma;
        }

        if (!double.IsFinite(weightedScore) || !double.IsFinite(totalWeight) || totalWeight <= 0)
        {
            return SoftScoreTrace.Zero;
        }

        var softScore = weightedScore / totalWeight;
        return new SoftScoreTrace(double.IsFinite(softScore) ? Math.Clamp(softScore, 0, 1) : 0, predictedDistances);
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

        var t = Math.Clamp(
            (speed - PlayerVisibilitySelectionParameters.MotionStartSpeed)
                / (PlayerVisibilitySelectionParameters.MotionFullSpeed - PlayerVisibilitySelectionParameters.MotionStartSpeed),
            0,
            1
        );
        return t * t * (3 - (2 * t));
    }

    private static long CalculateRetentionBonus(double motionFactor, PlayerVisibilitySelectionParameters parameters)
    {
        var bonus =
            PlayerVisibilitySelectionParameters.RestRetentionBonus
            + (
                (PlayerVisibilitySelectionParameters.MoveRetentionBonus - PlayerVisibilitySelectionParameters.RestRetentionBonus)
                * motionFactor
            );
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
        double SoftScore,
        Vector4 PredictedDistances,
        long SoftPoints,
        long RankPoints,
        long RetentionPoints,
        long BaseScore,
        long AdjustedScore,
        bool WasPreviouslySelected
    );

    private readonly record struct SoftScoreTrace(double Score, Vector4 PredictedDistances)
    {
        public static readonly SoftScoreTrace Zero = new(0, Vector4.Zero);
    }
}

internal sealed record PlayerVisibilitySelectionResult(int[] SelectedSourceIndices, IReadOnlyList<PlayerVisibilitySelectionScore> Scores);

internal readonly record struct PlayerVisibilitySelectionScore(
    int SourceIndex,
    double SoftScore,
    Vector4 PredictedDistances,
    double HalfScoreDistance,
    long SoftPoints,
    long RankPoints,
    long RetentionPoints,
    long TotalPoints
);

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
        Vector3? localPosition,
        float softScoreHalfDistance
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(legacyTargets);

        if (!localPosition.HasValue || !IsFinite(localPosition.Value))
        {
            return new PlayerVisibilitySelectionEvaluation(PlayerVisibilitySelectionStatus.Unavailable, [], []);
        }

        try
        {
            parameters.DistanceSigma = softScoreHalfDistance;
            return EvaluateCore(plan, legacyTargets, limitVisiblePlayerCount, visiblePlayerCountLimit, localPosition.Value);
        }
        catch (Exception exception)
        {
            reportFailure?.Invoke(exception);
            return new PlayerVisibilitySelectionEvaluation(PlayerVisibilitySelectionStatus.Failed, [], []);
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
        var selection = PlayerVisibilitySelector.Evaluate(snapshot, parameters);

        var selectedKeys = new PlayerVisibilitySelectionKey[selection.SelectedSourceIndices.Length];
        for (var selectedIndex = 0; selectedIndex < selection.SelectedSourceIndices.Length; selectedIndex++)
        {
            var sourceIndex = selection.SelectedSourceIndices[selectedIndex];
            var entry = plan.Entries[sourceIndex];
            selectedKeys[selectedIndex] = new(sourceIndex, entry.Identity, entry.ObjectIndex);
        }

        var status = localSpeedSmoother.HasVelocityEstimate
            ? PlayerVisibilitySelectionStatus.Ready
            : PlayerVisibilitySelectionStatus.Warmup;
        return new PlayerVisibilitySelectionEvaluation(status, Array.AsReadOnly(selectedKeys), selection.Scores);
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
    IReadOnlyList<PlayerVisibilitySelectionKey> SelectedKeys,
    IReadOnlyList<PlayerVisibilitySelectionScore> Scores
);

internal readonly record struct PlayerVisibilitySelectionKey(int SourceIndex, PlayerObjectIdentity Identity, int ObjectIndex);

internal enum PlayerVisibilitySelectionStatus
{
    Ready,
    Warmup,
    Unavailable,
    Failed,
}
