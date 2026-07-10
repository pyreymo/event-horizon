using System;
using System.Collections.Generic;
using EventHorizon.Culling.Rules;

namespace EventHorizon.Culling.Visibility;

internal static class PlayerVisibilityStableTargetBuilder
{
    public static PlayerVisibilityTargetSet Build(
        PlayerVisibilityPlan plan,
        IReadOnlyCollection<PlayerObjectIdentity> selectedCompetitiveIdentities,
        List<PlayerVisibilityTarget> targets
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selectedCompetitiveIdentities);
        ArgumentNullException.ThrowIfNull(targets);

        var competitiveIdentities = new HashSet<PlayerObjectIdentity>();
        foreach (var entry in plan.Entries)
        {
            if (entry.Classification == PlayerVisibilityClassification.Competitive)
            {
                competitiveIdentities.Add(entry.Identity);
            }
        }

        var selected = new HashSet<PlayerObjectIdentity>(selectedCompetitiveIdentities);
        foreach (var identity in selected)
        {
            if (!competitiveIdentities.Contains(identity))
            {
                throw new ArgumentException(
                    "Selected identity does not map to a Competitive entry in the current player visibility plan.",
                    nameof(selectedCompetitiveIdentities)
                );
            }
        }

        targets.Clear();
        foreach (var entry in plan.Entries)
        {
            if (!entry.IsManaged)
            {
                continue;
            }

            var desiredVisible = entry.Classification switch
            {
                PlayerVisibilityClassification.BypassVisible => true,
                PlayerVisibilityClassification.Competitive => selected.Contains(entry.Identity),
                PlayerVisibilityClassification.ForceHidden => false,
                _ => throw new ArgumentOutOfRangeException(nameof(entry.Classification), entry.Classification, null),
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
