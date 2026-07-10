using System;
using System.Collections.Generic;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GameEventHandler = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler;

namespace EventHorizon.Culling;

internal sealed unsafe class NonPlayerCuller(Configuration configuration)
{
    private const int MaxPlayerRelatedObjectIndex = 199;
    private readonly HashSet<uint> hiddenPlayerOwnerEntityIds = [];
    private readonly HashSet<uint> oddSlotPlayerOwnerIds = [];
    private readonly EventNpcRule eventNpcs = new();

    public void Refresh(GameObjectManager* manager) => eventNpcs.Refresh(manager, configuration.HideUnattachedEventNpcs);

    public void Tick(GameObjectManager* manager, HiddenObjectTracker hiddenObjects, VisibilityFlags hiddenFlags)
    {
        CollectHiddenPlayerOwnerEntityIds(manager, hiddenObjects);
        CollectOddSlotPlayerOwnerIds(manager);

        var maxIndex = Math.Min(EventNpcRule.LastSlot, manager->Objects.IndexSorted.Length - 1);
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

    public void Clear() => eventNpcs.Clear();

    private bool ShouldHide(GameObject* gameObject, int index)
    {
        if (IsLocalPlayerReservedSlot(index))
        {
            return false;
        }

        if (index is >= EventNpcRule.FirstSlot and <= EventNpcRule.LastSlot)
        {
            return eventNpcs.ShouldHide(gameObject, index);
        }

        if (IsPlayerRelatedEvenSlot(index))
        {
            return gameObject->ObjectKind == ObjectKind.BattleNpc
                && gameObject->OwnerId != 0
                && hiddenPlayerOwnerEntityIds.Contains(gameObject->OwnerId);
        }

        if (!IsPlayerRelatedOddSlot(index))
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
        var maxIndex = Math.Min(MaxPlayerRelatedObjectIndex, manager->Objects.IndexSorted.Length - 1);
        for (var index = 0; index <= maxIndex; index++)
        {
            if (!IsPlayerRelatedEvenSlot(index))
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

        var maxIndex = Math.Min(MaxPlayerRelatedObjectIndex, manager->Objects.IndexSorted.Length - 1);
        for (var index = 0; index <= maxIndex; index++)
        {
            if (!IsPlayerRelatedOddSlot(index) || IsLocalPlayerReservedSlot(index))
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

    private static bool IsPlayerRelatedSlot(int index) => index is >= 0 and <= MaxPlayerRelatedObjectIndex;

    private static bool IsPlayerRelatedEvenSlot(int index) => IsPlayerRelatedSlot(index) && index % 2 == 0;

    private static bool IsPlayerRelatedOddSlot(int index) => IsPlayerRelatedSlot(index) && index % 2 == 1;

    private static bool IsLocalPlayerReservedSlot(int index) => index is 0 or 1;

    private sealed class EventNpcRule
    {
        public const int FirstSlot = 489;
        public const int LastSlot = 608;
        private const int MaxEventHandlers = 32;
        private readonly nint[] hiddenObjectAddresses = new nint[LastSlot - FirstSlot + 1];

        public void Refresh(GameObjectManager* manager, bool enabled)
        {
            Array.Clear(hiddenObjectAddresses);
            if (!enabled || manager == null)
            {
                return;
            }

            var lastSlot = Math.Min(LastSlot, manager->Objects.IndexSorted.Length - 1);
            for (var slot = FirstSlot; slot <= lastSlot; slot++)
            {
                var gameObject = manager->Objects.IndexSorted[slot].Value;
                if (gameObject != null && Evaluate(gameObject))
                {
                    hiddenObjectAddresses[slot - FirstSlot] = (nint)gameObject;
                }
            }
        }

        public bool ShouldHide(GameObject* gameObject, int slot) =>
            slot is >= FirstSlot and <= LastSlot && hiddenObjectAddresses[slot - FirstSlot] == (nint)gameObject;

        public void Clear() => Array.Clear(hiddenObjectAddresses);

        private static bool Evaluate(GameObject* gameObject)
        {
            if (gameObject->ObjectKind != ObjectKind.EventNpc)
            {
                return false;
            }

            if ((gameObject->TargetableStatus & ObjectTargetableFlags.IsTargetable) == 0)
            {
                return true;
            }

            var handlers = stackalloc GameEventHandler*[MaxEventHandlers];
            var handlerCount = Math.Clamp(gameObject->GetEventHandlersImpl(handlers), 0, MaxEventHandlers);
            if (handlerCount == 0)
            {
                return false;
            }

            var hasDialogueHandler = false;
            for (var index = 0; index < handlerCount; index++)
            {
                var handler = handlers[index];
                if (handler == null)
                {
                    return false;
                }

                switch (handler->Info.EventId.ContentId)
                {
                    case EventHandlerContent.DefaultTalk:
                    case EventHandlerContent.CustomTalk:
                        hasDialogueHandler = true;
                        break;
                    case EventHandlerContent.Quest:
                        if (handler->GetNameplateIconForObject(gameObject) != 0)
                        {
                            return false;
                        }
                        break;
                    default:
                        return false;
                }
            }

            return hasDialogueHandler;
        }
    }
}
