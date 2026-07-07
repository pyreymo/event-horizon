using System;
using System.Collections.Generic;
using EventHorizon.Culling.Rules;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling.Visibility;

internal sealed unsafe class PlayerVisibilityPlan
{
    private PlayerVisibilityPlan(int revision, IReadOnlyList<PlayerVisibilityIntent> intents, PlayerKeepBudgetStats budgetStats)
    {
        Revision = revision;
        Intents = intents;
        BudgetStats = budgetStats;
    }

    public int Revision { get; }
    public IReadOnlyList<PlayerVisibilityIntent> Intents { get; }
    public PlayerKeepBudgetStats BudgetStats { get; }

    public static PlayerVisibilityPlan Build(
        int revision,
        Configuration configuration,
        GameObjectManager* manager,
        PlayerKeepPlan keepPlan,
        uint? previewVisibleEntityId,
        List<PlayerVisibilityIntent> intents
    )
    {
        intents.Clear();

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject == null || gameObject->ObjectKind != ObjectKind.Pc)
            {
                continue;
            }

            var address = (nint)gameObject;
            var keepDecision = keepPlan.GetDecision(address);
            var desiredVisible = !ShouldHidePlayerSlotObject(gameObject, index, keepPlan);
            if (previewVisibleEntityId == gameObject->EntityId)
            {
                desiredVisible = true;
            }

            var intent = new PlayerVisibilityIntent(
                PlayerObjectIdentity.From(gameObject),
                index,
                desiredVisible,
                keepDecision,
                keepPlan.IsCutByBudget(address)
            );
            intents.Add(intent);
        }

        return new PlayerVisibilityPlan(
            revision,
            intents,
            new PlayerKeepBudgetStats(
                keepPlan.BudgetExemptPlayerCount,
                keepPlan.VisibleBudgetedPlayerCount,
                Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 100)
            )
        );
    }

    private static bool ShouldHidePlayerSlotObject(GameObject* gameObject, int index, PlayerKeepPlan keepPlan)
    {
        if (!IsPlayerRelatedEvenSlot(index) || IsLocalPlayerReservedSlot(index))
        {
            return false;
        }

        return keepPlan.ShouldHide((nint)gameObject);
    }

    private static bool IsPlayerRelatedSlot(int index) => index is >= 0 and <= 199;

    private static bool IsPlayerRelatedEvenSlot(int index) => IsPlayerRelatedSlot(index) && index % 2 == 0;

    private static bool IsLocalPlayerReservedSlot(int index) => index is 0 or 1;
}

internal readonly record struct PlayerVisibilityIntent(
    PlayerObjectIdentity Identity,
    int ObjectIndex,
    bool DesiredVisible,
    PlayerKeepDecision Decision,
    bool CutByBudget
);

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
