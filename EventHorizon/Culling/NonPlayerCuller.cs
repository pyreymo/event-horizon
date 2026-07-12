using System;
using System.Collections.Generic;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class NonPlayerCuller(Configuration configuration)
{
    private readonly HashSet<uint> hiddenPlayerOwnerEntityIds = [];
    private readonly HashSet<uint> oddSlotPlayerOwnerIds = [];

    public void Tick(GameObjectManager* manager, HiddenObjectTracker hiddenObjects, VisibilityFlags hiddenFlags)
    {
        CollectHiddenPlayerOwnerEntityIds(manager, hiddenObjects);
        CollectOddSlotPlayerOwnerIds(manager);

        var maxIndex = Math.Min(PlayerObjectSlots.LastPlayerRelated, manager->Objects.IndexSorted.Length - 1);
        for (var index = 0; index <= maxIndex; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject == null || gameObject->ObjectKind == ObjectKind.Pc)
            {
                continue;
            }

            if (ShouldHide(gameObject, index))
            {
                hiddenObjects.Hide(gameObject, hiddenFlags, index);
            }
            else
            {
                hiddenObjects.RestoreIfHidden(gameObject);
            }
        }

        hiddenPlayerOwnerEntityIds.Clear();
        oddSlotPlayerOwnerIds.Clear();
    }

    public void Clear()
    {
        hiddenPlayerOwnerEntityIds.Clear();
        oddSlotPlayerOwnerIds.Clear();
    }

    private bool ShouldHide(GameObject* gameObject, int index)
    {
        if (PlayerObjectSlots.IsLocalReserved(index))
        {
            return false;
        }

        if (PlayerObjectSlots.IsPlayer(index))
        {
            return gameObject->ObjectKind == ObjectKind.BattleNpc
                && gameObject->OwnerId != 0
                && hiddenPlayerOwnerEntityIds.Contains(gameObject->OwnerId);
        }

        if (!PlayerObjectSlots.IsAttached(index))
        {
            return false;
        }

        if (
            configuration.HideOtherPlayerBattlePets
            && gameObject->ObjectKind == ObjectKind.BattleNpc
            && gameObject->OwnerId != 0
            && oddSlotPlayerOwnerIds.Contains(gameObject->OwnerId)
        )
        {
            return true;
        }

        return gameObject->ObjectKind switch
        {
            ObjectKind.Companion => configuration.HideOtherPlayerCompanions,
            ObjectKind.Ornament => configuration.HideOtherPlayerOrnaments,
            _ => false,
        };
    }

    private void CollectHiddenPlayerOwnerEntityIds(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
        hiddenPlayerOwnerEntityIds.Clear();
        var maxIndex = Math.Min(PlayerObjectSlots.LastPlayerRelated, manager->Objects.IndexSorted.Length - 1);
        for (var index = 0; index <= maxIndex; index++)
        {
            if (!PlayerObjectSlots.IsPlayer(index))
            {
                continue;
            }

            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject != null && gameObject->ObjectKind == ObjectKind.Pc && hiddenObjects.IsHidden(gameObject))
            {
                hiddenPlayerOwnerEntityIds.Add(gameObject->EntityId);
            }
        }
    }

    private void CollectOddSlotPlayerOwnerIds(GameObjectManager* manager)
    {
        oddSlotPlayerOwnerIds.Clear();
        if (!configuration.HideOtherPlayerBattlePets)
        {
            return;
        }

        var maxIndex = Math.Min(PlayerObjectSlots.LastPlayerRelated, manager->Objects.IndexSorted.Length - 1);
        for (var index = 0; index <= maxIndex; index++)
        {
            if (!PlayerObjectSlots.IsAttached(index) || PlayerObjectSlots.IsLocalReserved(index))
            {
                continue;
            }

            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject == null || gameObject->ObjectKind != ObjectKind.Pc)
            {
                continue;
            }

            oddSlotPlayerOwnerIds.Add(gameObject->EntityId);
            var gameObjectId = (ulong)gameObject->GetGameObjectId();
            if (gameObjectId <= uint.MaxValue)
            {
                oddSlotPlayerOwnerIds.Add((uint)gameObjectId);
            }
        }
    }
}
