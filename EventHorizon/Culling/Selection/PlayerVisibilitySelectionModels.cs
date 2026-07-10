using System;
using System.Collections.Generic;
using System.Numerics;

namespace EventHorizon.Culling.Selection;

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

internal readonly record struct PlayerVisibilityScoredCandidate(
    int SourceIndex,
    ulong GameObjectId,
    uint EntityId,
    int ObjectIndex,
    int Rank,
    double SoftScore,
    long SoftPoints,
    long BaseScore,
    long AdjustedScore,
    bool WasPreviouslySelected
);

internal sealed record PlayerVisibilitySelectionResult(
    IReadOnlyList<int> SelectedSourceIndices,
    IReadOnlyList<PlayerVisibilityScoredCandidate> RankedCandidates,
    int Budget,
    int CandidateCount,
    int SelectedCount,
    int PreviouslySelectedCount,
    int RetainedCount,
    int EnteredCount,
    double MotionFactor,
    long RetentionBonus
);
