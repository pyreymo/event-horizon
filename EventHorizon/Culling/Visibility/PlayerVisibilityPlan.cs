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
        IReadOnlyList<PlayerVisibilityIntent> intents,
        PlayerKeepBudgetStats budgetStats,
        PlayerVisibilityClassificationCounts classificationCounts
    )
    {
        Revision = revision;
        Intents = intents;
        BudgetStats = budgetStats;
        ClassificationCounts = classificationCounts;
    }

    public int Revision { get; }
    public IReadOnlyList<PlayerVisibilityIntent> Intents { get; }
    public PlayerKeepBudgetStats BudgetStats { get; }
    public PlayerVisibilityClassificationCounts ClassificationCounts { get; }

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
            var desiredVisible = GetDesiredVisible(classification, address, keepPlan);
            var cutByBudget = keepPlan.IsCutByBudget(address);

            var intent = new PlayerVisibilityIntent(
                PlayerObjectIdentity.From(gameObject),
                index,
                classification,
                desiredVisible,
                keepDecision,
                cutByBudget
            );
            intents.Add(intent);

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
            intents,
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

    private static bool GetDesiredVisible(PlayerVisibilityClassification classification, nint address, PlayerKeepPlan keepPlan) =>
        classification switch
        {
            PlayerVisibilityClassification.BypassVisible => true,
            PlayerVisibilityClassification.Competitive => !keepPlan.IsCutByBudget(address),
            PlayerVisibilityClassification.ForceHidden => false,
            PlayerVisibilityClassification.Unmanaged => true,
            _ => true,
        };

    private static bool IsPlayerRelatedSlot(int index) => index is >= 0 and <= 199;

    private static bool IsPlayerRelatedEvenSlot(int index) => IsPlayerRelatedSlot(index) && index % 2 == 0;

    private static bool IsLocalPlayerReservedSlot(int index) => index is 0 or 1;
}

internal readonly record struct PlayerVisibilityIntent(
    PlayerObjectIdentity Identity,
    int ObjectIndex,
    PlayerVisibilityClassification Classification,
    bool DesiredVisible,
    PlayerKeepDecision Decision,
    bool CutByBudget
)
{
    public bool IsManaged => Classification != PlayerVisibilityClassification.Unmanaged;
}

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
