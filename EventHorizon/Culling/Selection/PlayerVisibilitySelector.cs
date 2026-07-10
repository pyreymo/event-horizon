using System;
using System.Collections.Generic;
using System.Numerics;

namespace EventHorizon.Culling.Selection;

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
