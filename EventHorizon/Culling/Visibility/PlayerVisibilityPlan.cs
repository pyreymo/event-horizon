using System;
using System.Collections.Generic;
using System.Numerics;
using EventHorizon.Culling.Rules;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling.Visibility;

internal sealed unsafe class PlayerVisibilityPlan
{
    internal PlayerVisibilityPlan(
        int generation,
        long createdAtTickCount64,
        IReadOnlyList<PlayerVisibilityPlanEntry> entries,
        PlayerVisibilityClassificationCounts classificationCounts
    )
    {
        Generation = generation;
        CreatedAtTickCount64 = createdAtTickCount64;
        Entries = entries;
        ClassificationCounts = classificationCounts;
    }

    public int Generation { get; }
    public long CreatedAtTickCount64 { get; }
    public IReadOnlyList<PlayerVisibilityPlanEntry> Entries { get; }
    public PlayerVisibilityClassificationCounts ClassificationCounts { get; }

    public static PlayerVisibilityPlan Build(
        int generation,
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
            var hasPosition = TryGetPosition(gameObject, out var position);

            var entry = new PlayerVisibilityPlanEntry(
                PlayerObjectIdentity.From(gameObject),
                index,
                classification,
                keepDecision,
                cutByBudget,
                position,
                hasPosition
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
            generation,
            Environment.TickCount64,
            [.. entries],
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

    private static bool TryGetPosition(GameObject* gameObject, out Vector3 position)
    {
        position = default;
        if (gameObject == null || gameObject->VirtualTable == null)
        {
            return false;
        }

        var positionPtr = gameObject->GetPosition();
        if (positionPtr == null)
        {
            return false;
        }

        position = (Vector3)(*positionPtr);
        return true;
    }
}

internal readonly record struct PlayerVisibilityPlanEntry(
    PlayerObjectIdentity Identity,
    int ObjectIndex,
    PlayerVisibilityClassification Classification,
    PlayerKeepDecision Decision,
    bool CutByBudget,
    Vector3 Position,
    bool HasPosition
)
{
    public bool IsManaged => Classification != PlayerVisibilityClassification.Unmanaged;
}

internal sealed class PlayerVisibilityTargetSet(
    int generation,
    long createdAtTickCount64,
    IReadOnlyList<PlayerVisibilityTarget> targets,
    PlayerVisibilityClassificationCounts classificationCounts
)
{
    public int Generation { get; } = generation;
    public long CreatedAtTickCount64 { get; } = createdAtTickCount64;
    public IReadOnlyList<PlayerVisibilityTarget> Targets { get; } = targets;
    public PlayerVisibilityClassificationCounts ClassificationCounts { get; } = classificationCounts;

    public long GetAgeMilliseconds(long nowTickCount64) => Math.Max(0, nowTickCount64 - CreatedAtTickCount64);
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

        return new PlayerVisibilityTargetSet(plan.Generation, plan.CreatedAtTickCount64, [.. targets], plan.ClassificationCounts);
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
