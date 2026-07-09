using System;
using System.Collections.Generic;
using EventHorizon.Culling.Rules;
using EventHorizon.Settings;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerVisibilitySolverSnapshot
{
    private PlayerVisibilitySolverSnapshot(
        int generation,
        long createdAtTickCount64,
        int competitiveBudget,
        IReadOnlyList<PlayerVisibilitySolverPlayer> competitivePlayers,
        PlayerVisibilityClassificationCounts classificationCounts
    )
    {
        Generation = generation;
        CreatedAtTickCount64 = createdAtTickCount64;
        CompetitiveBudget = competitiveBudget;
        CompetitivePlayers = competitivePlayers;
        ClassificationCounts = classificationCounts;
    }

    public int Generation { get; }
    public long CreatedAtTickCount64 { get; }
    public int CompetitiveBudget { get; }
    public IReadOnlyList<PlayerVisibilitySolverPlayer> CompetitivePlayers { get; }
    public PlayerVisibilityClassificationCounts ClassificationCounts { get; }

    public long GetAgeMilliseconds(long nowTickCount64) => Math.Max(0, nowTickCount64 - CreatedAtTickCount64);

    public static PlayerVisibilitySolverSnapshot Build(
        Configuration configuration,
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet? previousTargetSet,
        List<PlayerVisibilitySolverPlayer> competitivePlayers
    )
    {
        competitivePlayers.Clear();
        foreach (var entry in plan.Entries)
        {
            if (entry.Classification != PlayerVisibilityClassification.Competitive)
            {
                continue;
            }

            var legacyTargetVisible = !entry.CutByBudget;
            competitivePlayers.Add(
                new PlayerVisibilitySolverPlayer(
                    entry.Identity,
                    entry.ObjectIndex,
                    entry.Decision,
                    GetPreviousTargetVisible(previousTargetSet, entry.Identity, legacyTargetVisible),
                    legacyTargetVisible,
                    entry.CutByBudget
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
    bool CutByBudget
);
