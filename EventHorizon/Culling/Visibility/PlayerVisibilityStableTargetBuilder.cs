using System;
using System.Collections.Generic;
using EventHorizon.Culling.Rules;

namespace EventHorizon.Culling.Visibility;

internal static class PlayerVisibilityStableTargetBuilder
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

        return new PlayerVisibilityTargetSet(plan.Generation, plan.CreatedAtTickCount64, [.. targets], plan.ClassificationCounts);
    }
}

internal static class PlayerVisibilityActiveBudgetStats
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
