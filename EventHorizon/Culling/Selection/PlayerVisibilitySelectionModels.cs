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

internal sealed record PlayerVisibilitySelectionResult(IReadOnlyList<int> SelectedSourceIndices);
