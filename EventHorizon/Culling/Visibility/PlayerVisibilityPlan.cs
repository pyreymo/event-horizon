using System;
using System.Collections.Generic;
using EventHorizon.Culling.Rules;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling.Visibility;

internal sealed unsafe class PlayerVisibilityPlan
{
    private PlayerVisibilityPlan(
        int revision,
        IReadOnlyList<PlayerVisibilityPlanEntry> entries,
        PlayerKeepBudgetStats budgetStats,
        PlayerVisibilityClassificationCounts classificationCounts
    )
    {
        Revision = revision;
        Entries = entries;
        BudgetStats = budgetStats;
        ClassificationCounts = classificationCounts;
    }

    public int Revision { get; }
    public IReadOnlyList<PlayerVisibilityPlanEntry> Entries { get; }
    public PlayerKeepBudgetStats BudgetStats { get; }
    public PlayerVisibilityClassificationCounts ClassificationCounts { get; }

    public static PlayerVisibilityPlan Build(
        int revision,
        Configuration configuration,
        GameObjectManager* manager,
        PlayerKeepPlan keepPlan,
        uint? previewVisibleEntityId,
        List<PlayerVisibilityPlanEntry> entries
    )
    {
        entries.Clear();
        var bypassVisibleCount = 0;
        var competitiveCount = 0;
        var forceHiddenCount = 0;
        var unmanagedCount = 0;

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject == null || gameObject->ObjectKind != ObjectKind.Pc)
            {
                continue;
            }

            var address = (nint)gameObject;
            var keepDecision = keepPlan.GetDecision(address);
            var classification = Classify(index, keepDecision, previewVisibleEntityId == gameObject->EntityId);
            var cutByBudget = keepPlan.IsCutByBudget(address);

            var entry = new PlayerVisibilityPlanEntry(
                PlayerObjectIdentity.From(gameObject),
                index,
                classification,
                keepDecision,
                cutByBudget
            );
            entries.Add(entry);

            switch (classification)
            {
                case PlayerVisibilityClassification.BypassVisible:
                    bypassVisibleCount++;
                    break;
                case PlayerVisibilityClassification.Competitive:
                    competitiveCount++;
                    break;
                case PlayerVisibilityClassification.ForceHidden:
                    forceHiddenCount++;
                    break;
                case PlayerVisibilityClassification.Unmanaged:
                    unmanagedCount++;
                    break;
            }
        }

        return new PlayerVisibilityPlan(
            revision,
            entries,
            new PlayerKeepBudgetStats(
                keepPlan.BudgetExemptPlayerCount,
                keepPlan.VisibleBudgetedPlayerCount,
                Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 100)
            ),
            new PlayerVisibilityClassificationCounts(bypassVisibleCount, competitiveCount, forceHiddenCount, unmanagedCount)
        );
    }

    private static PlayerVisibilityClassification Classify(int index, PlayerKeepDecision keepDecision, bool previewVisible)
    {
        if (!IsPlayerRelatedEvenSlot(index) || IsLocalPlayerReservedSlot(index))
        {
            return PlayerVisibilityClassification.Unmanaged;
        }

        if (previewVisible)
        {
            return PlayerVisibilityClassification.BypassVisible;
        }

        return keepDecision.Kind switch
        {
            PlayerKeepDecisionKind.Keep when keepDecision.BudgetPolicy == PlayerKeepBudgetPolicy.Exempt =>
                PlayerVisibilityClassification.BypassVisible,
            PlayerKeepDecisionKind.Keep when keepDecision.BudgetPolicy == PlayerKeepBudgetPolicy.Counted =>
                PlayerVisibilityClassification.Competitive,
            _ => PlayerVisibilityClassification.ForceHidden,
        };
    }

    private static bool IsPlayerRelatedSlot(int index) => index is >= 0 and <= 199;

    private static bool IsPlayerRelatedEvenSlot(int index) => IsPlayerRelatedSlot(index) && index % 2 == 0;

    private static bool IsLocalPlayerReservedSlot(int index) => index is 0 or 1;
}

internal readonly record struct PlayerVisibilityPlanEntry(
    PlayerObjectIdentity Identity,
    int ObjectIndex,
    PlayerVisibilityClassification Classification,
    PlayerKeepDecision Decision,
    bool CutByBudget
)
{
    public bool IsManaged => Classification != PlayerVisibilityClassification.Unmanaged;
}

internal sealed class PlayerVisibilityTargetSet
{
    public PlayerVisibilityTargetSet(
        int revision,
        IReadOnlyList<PlayerVisibilityTarget> targets,
        PlayerVisibilityClassificationCounts classificationCounts
    )
    {
        Revision = revision;
        Targets = targets;
        ClassificationCounts = classificationCounts;
    }

    public int Revision { get; }
    public IReadOnlyList<PlayerVisibilityTarget> Targets { get; }
    public PlayerVisibilityClassificationCounts ClassificationCounts { get; }
}

internal static class PlayerVisibilityLegacyTargetBuilder
{
    public static PlayerVisibilityTargetSet Build(PlayerVisibilityPlan plan, List<PlayerVisibilityTarget> targets)
    {
        targets.Clear();
        foreach (var entry in plan.Entries)
        {
            if (!entry.IsManaged)
            {
                continue;
            }

            targets.Add(
                new PlayerVisibilityTarget(
                    entry.Identity,
                    entry.ObjectIndex,
                    entry.Classification,
                    GetDesiredVisible(entry),
                    entry.Decision,
                    entry.CutByBudget
                )
            );
        }

        return new PlayerVisibilityTargetSet(plan.Revision, [.. targets], plan.ClassificationCounts);
    }

    private static bool GetDesiredVisible(PlayerVisibilityPlanEntry entry) =>
        entry.Classification switch
        {
            PlayerVisibilityClassification.BypassVisible => true,
            PlayerVisibilityClassification.Competitive => !entry.CutByBudget,
            PlayerVisibilityClassification.ForceHidden => false,
            PlayerVisibilityClassification.Unmanaged => true,
            _ => true,
        };
}

internal readonly record struct PlayerVisibilityTarget(
    PlayerObjectIdentity Identity,
    int ObjectIndex,
    PlayerVisibilityClassification Classification,
    bool DesiredVisible,
    PlayerKeepDecision Decision,
    bool CutByBudget
);

internal readonly record struct PlayerVisibilityClassificationCounts(int BypassVisible, int Competitive, int ForceHidden, int Unmanaged);

internal enum PlayerVisibilityClassification
{
    BypassVisible,
    Competitive,
    ForceHidden,
    Unmanaged,
}

internal readonly record struct PlayerObjectIdentity(nint Address, ulong GameObjectId, uint EntityId)
{
    public static unsafe PlayerObjectIdentity From(GameObject* gameObject) =>
        new((nint)gameObject, (ulong)gameObject->GetGameObjectId(), gameObject->EntityId);

    public unsafe bool Matches(GameObject* gameObject) =>
        gameObject != null
        && (nint)gameObject == Address
        && (ulong)gameObject->GetGameObjectId() == GameObjectId
        && gameObject->EntityId == EntityId;
}
