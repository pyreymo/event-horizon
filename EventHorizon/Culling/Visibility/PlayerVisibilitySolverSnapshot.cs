using System;
using System.Collections.Generic;
using System.Numerics;
using EventHorizon.Culling.Rules;
using EventHorizon.Settings;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerVisibilitySolverSnapshot
{
    private PlayerVisibilitySolverSnapshot(
        int generation,
        long createdAtTickCount64,
        int competitiveBudget,
        int positionSampleCount,
        IReadOnlyList<PlayerVisibilitySolverPlayer> competitivePlayers,
        PlayerVisibilityClassificationCounts classificationCounts
    )
    {
        Generation = generation;
        CreatedAtTickCount64 = createdAtTickCount64;
        CompetitiveBudget = competitiveBudget;
        PositionSampleCount = positionSampleCount;
        CompetitivePlayers = competitivePlayers;
        ClassificationCounts = classificationCounts;
    }

    public int Generation { get; }
    public long CreatedAtTickCount64 { get; }
    public int CompetitiveBudget { get; }
    public int PositionSampleCount { get; }
    public IReadOnlyList<PlayerVisibilitySolverPlayer> CompetitivePlayers { get; }
    public PlayerVisibilityClassificationCounts ClassificationCounts { get; }

    public long GetAgeMilliseconds(long nowTickCount64) => Math.Max(0, nowTickCount64 - CreatedAtTickCount64);

    public static PlayerVisibilitySolverSnapshot Build(
        Configuration configuration,
        PlayerVisibilityPlan plan,
        PlayerVisibilityMotionTracker motionTracker,
        PlayerVisibilityTargetSet? previousTargetSet,
        List<PlayerVisibilitySolverPlayer> competitivePlayers
    )
    {
        competitivePlayers.Clear();
        var positionSampleCount = 0;
        foreach (var entry in plan.Entries)
        {
            if (entry.Classification != PlayerVisibilityClassification.Competitive)
            {
                continue;
            }

            if (entry.HasPosition)
            {
                positionSampleCount++;
            }

            var legacyTargetVisible = !entry.CutByBudget;
            var hasVelocitySample = motionTracker.TryGetVelocityPerSecond(entry.Identity, out var velocityPerSecond);
            competitivePlayers.Add(
                new PlayerVisibilitySolverPlayer(
                    entry.Identity,
                    entry.ObjectIndex,
                    entry.Decision,
                    GetPreviousTargetVisible(previousTargetSet, entry.Identity, legacyTargetVisible),
                    legacyTargetVisible,
                    entry.CutByBudget,
                    entry.Position,
                    velocityPerSecond,
                    hasVelocitySample,
                    entry.HasPosition
                )
            );
        }

        var budget = configuration.LimitVisiblePlayerCount
            ? Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 100)
            : competitivePlayers.Count;

        return new PlayerVisibilitySolverSnapshot(
            plan.Generation,
            plan.CreatedAtTickCount64,
            Math.Min(budget, competitivePlayers.Count),
            positionSampleCount,
            [.. competitivePlayers],
            plan.ClassificationCounts
        );
    }

    private static bool GetPreviousTargetVisible(
        PlayerVisibilityTargetSet? previousTargetSet,
        PlayerObjectIdentity identity,
        bool fallbackVisible
    )
    {
        if (previousTargetSet == null)
        {
            return fallbackVisible;
        }

        foreach (var target in previousTargetSet.Targets)
        {
            if (target.Identity.Equals(identity))
            {
                return target.DesiredVisible;
            }
        }

        return fallbackVisible;
    }
}

internal readonly record struct PlayerVisibilitySolverPlayer(
    PlayerObjectIdentity Identity,
    int ObjectIndex,
    PlayerKeepDecision Decision,
    bool PreviousTargetVisible,
    bool LegacyTargetVisible,
    bool CutByBudget,
    Vector3 Position,
    Vector3 VelocityPerSecond,
    bool HasVelocitySample,
    bool HasPosition
);

internal sealed class PlayerVisibilityMotionTracker
{
    private const long MaxVelocitySampleAgeMs = 1_000;

    private readonly Dictionary<ulong, PlayerVisibilityMotionSample> samples = [];
    private readonly HashSet<ulong> liveGameObjectIds = [];
    private readonly List<ulong> staleGameObjectIds = [];

    public void Update(PlayerVisibilityPlan plan)
    {
        liveGameObjectIds.Clear();
        foreach (var entry in plan.Entries)
        {
            if (!entry.HasPosition || entry.Identity.GameObjectId == 0)
            {
                continue;
            }

            var gameObjectId = entry.Identity.GameObjectId;
            liveGameObjectIds.Add(gameObjectId);
            var velocityPerSecond = Vector3.Zero;
            var hasVelocitySample = false;
            if (samples.TryGetValue(gameObjectId, out var previous))
            {
                var elapsedMs = plan.CreatedAtTickCount64 - previous.CreatedAtTickCount64;
                if (elapsedMs is > 0 and <= MaxVelocitySampleAgeMs)
                {
                    velocityPerSecond = (entry.Position - previous.Position) * (1000f / elapsedMs);
                    hasVelocitySample = true;
                }
            }

            samples[gameObjectId] = new PlayerVisibilityMotionSample(
                entry.Position,
                velocityPerSecond,
                hasVelocitySample,
                plan.CreatedAtTickCount64
            );
        }

        PruneMissing();
    }

    public bool TryGetVelocityPerSecond(PlayerObjectIdentity identity, out Vector3 velocityPerSecond)
    {
        velocityPerSecond = Vector3.Zero;
        if (identity.GameObjectId == 0 || !samples.TryGetValue(identity.GameObjectId, out var sample))
        {
            return false;
        }

        velocityPerSecond = sample.VelocityPerSecond;
        return sample.HasVelocitySample;
    }

    public void Clear()
    {
        samples.Clear();
        liveGameObjectIds.Clear();
        staleGameObjectIds.Clear();
    }

    private void PruneMissing()
    {
        staleGameObjectIds.Clear();
        foreach (var gameObjectId in samples.Keys)
        {
            if (!liveGameObjectIds.Contains(gameObjectId))
            {
                staleGameObjectIds.Add(gameObjectId);
            }
        }

        foreach (var gameObjectId in staleGameObjectIds)
        {
            samples.Remove(gameObjectId);
        }

        staleGameObjectIds.Clear();
    }
}

internal readonly record struct PlayerVisibilityMotionSample(
    Vector3 Position,
    Vector3 VelocityPerSecond,
    bool HasVelocitySample,
    long CreatedAtTickCount64
);
