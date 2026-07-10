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

internal sealed record PlayerVisibilitySelectionResult(IReadOnlyList<int> SelectedSourceIndices);

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

    public static PlayerVisibilitySelectionParameters Default { get; } = new();

    public PlayerVisibilitySelectionParameters(
        int rankCount = DefaultRankCount,
        long rankStep = DefaultRankStep,
        long softScoreScale = DefaultSoftScoreScale,
        long restRetentionBonus = DefaultRestRetentionBonus,
        long moveRetentionBonus = DefaultMoveRetentionBonus,
        int predictionSteps = DefaultPredictionSteps,
        double predictionStepSeconds = DefaultPredictionStepSeconds,
        double predictionGamma = DefaultPredictionGamma,
        double distanceSigma = DefaultDistanceSigma,
        double motionStartSpeed = DefaultMotionStartSpeed,
        double motionFullSpeed = DefaultMotionFullSpeed,
        double localSpeedHalfLifeSeconds = DefaultLocalSpeedHalfLifeSeconds,
        double maxTrustedLocalSpeed = DefaultMaxTrustedLocalSpeed
    )
    {
        if (rankCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(rankCount), rankCount, "RankCount must be at least 2.");
        }

        if (softScoreScale < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(softScoreScale), softScoreScale, "SoftScoreScale cannot be negative.");
        }

        if (restRetentionBonus < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(restRetentionBonus), restRetentionBonus, "RestRetentionBonus cannot be negative.");
        }

        if (rankStep <= (decimal)softScoreScale + restRetentionBonus)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rankStep),
                rankStep,
                "RankStep must be greater than SoftScoreScale + RestRetentionBonus."
            );
        }

        var maxBaseScore = ((decimal)(rankCount - 1) * rankStep) + softScoreScale;
        if (moveRetentionBonus <= maxBaseScore)
        {
            throw new ArgumentOutOfRangeException(
                nameof(moveRetentionBonus),
                moveRetentionBonus,
                "MoveRetentionBonus must be greater than (RankCount - 1) * RankStep + SoftScoreScale."
            );
        }

        if (maxBaseScore + moveRetentionBonus > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(moveRetentionBonus),
                moveRetentionBonus,
                "MaxBaseScore + MoveRetentionBonus must not exceed Int64.MaxValue."
            );
        }

        if (predictionSteps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(predictionSteps), predictionSteps, "PredictionSteps must be at least 1.");
        }

        RequireFinitePositive(predictionStepSeconds, nameof(predictionStepSeconds));
        if (!double.IsFinite(predictionGamma) || predictionGamma <= 0 || predictionGamma > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(predictionGamma), predictionGamma, "PredictionGamma must be in (0, 1].");
        }

        RequireFinitePositive(distanceSigma, nameof(distanceSigma));
        if (!double.IsFinite(motionStartSpeed) || motionStartSpeed < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(motionStartSpeed),
                motionStartSpeed,
                "MotionStartSpeed must be finite and non-negative."
            );
        }

        if (!double.IsFinite(motionFullSpeed) || motionFullSpeed <= motionStartSpeed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(motionFullSpeed),
                motionFullSpeed,
                "MotionFullSpeed must be finite and greater than MotionStartSpeed."
            );
        }

        RequireFinitePositive(localSpeedHalfLifeSeconds, nameof(localSpeedHalfLifeSeconds));
        RequireFinitePositive(maxTrustedLocalSpeed, nameof(maxTrustedLocalSpeed));

        RankCount = rankCount;
        RankStep = rankStep;
        SoftScoreScale = softScoreScale;
        RestRetentionBonus = restRetentionBonus;
        MoveRetentionBonus = moveRetentionBonus;
        PredictionSteps = predictionSteps;
        PredictionStepSeconds = predictionStepSeconds;
        PredictionGamma = predictionGamma;
        DistanceSigma = distanceSigma;
        MotionStartSpeed = motionStartSpeed;
        MotionFullSpeed = motionFullSpeed;
        LocalSpeedHalfLifeSeconds = localSpeedHalfLifeSeconds;
        MaxTrustedLocalSpeed = maxTrustedLocalSpeed;
    }

    public int RankCount { get; }
    public long RankStep { get; }
    public long SoftScoreScale { get; }
    public long RestRetentionBonus { get; }
    public long MoveRetentionBonus { get; }
    public int PredictionSteps { get; }
    public double PredictionStepSeconds { get; }
    public double PredictionGamma { get; }
    public double DistanceSigma { get; }
    public double MotionStartSpeed { get; }
    public double MotionFullSpeed { get; }
    public double LocalSpeedHalfLifeSeconds { get; }
    public double MaxTrustedLocalSpeed { get; }

    private static void RequireFinitePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} must be finite and positive.");
        }
    }
}

internal static class PlayerVisibilitySelector
{
    public static PlayerVisibilitySelectionResult Select(
        PlayerVisibilitySelectionSnapshot snapshot,
        PlayerVisibilitySelectionParameters parameters
    )
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

        return new PlayerVisibilitySelectionResult(Array.AsReadOnly(selectedSourceIndices));
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
