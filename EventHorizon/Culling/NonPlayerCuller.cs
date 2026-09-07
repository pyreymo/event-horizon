using System;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class NonPlayerCuller(Configuration configuration)
{
    private readonly EventNpcRule eventNpcs = new();
    private uint localPlayerEntityId;

    public void Refresh(GameObjectManager* manager) => eventNpcs.Refresh(manager, configuration.HideUnattachedEventNpcs);

    public void Tick(GameObjectManager* manager, HiddenObjectTracker hiddenObjects, VisibilityFlags hiddenFlags)
    {
        localPlayerEntityId = GetLocalPlayerEntityId(manager);

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
    }

    public void Clear()
    {
        eventNpcs.Clear();
        localPlayerEntityId = 0;
    }

    private bool ShouldHide(GameObject* gameObject, int index)
    {
        if (CharacterObjectSlots.IsLocalReservedSlot(index))
        {
            return false;
        }

        if (index is >= EventNpcRule.FirstSlot and <= EventNpcRule.LastSlot)
        {
            return eventNpcs.ShouldHide(gameObject, index);
        }

        if (CharacterObjectSlots.IsEvenSlot(index))
        {
            return configuration.HideOtherPlayerBattlePets
                && localPlayerEntityId != 0
                && gameObject->ObjectKind == ObjectKind.BattleNpc
                && gameObject->BattleNpcSubKind == BattleNpcSubKind.Pet
                && gameObject->OwnerId != localPlayerEntityId;
        }

        if (CharacterObjectSlots.IsOddSlot(index))
        {
            return gameObject->ObjectKind switch
            {
                ObjectKind.Companion => configuration.HideOtherPlayerCompanions,
                ObjectKind.Ornament => configuration.HideOtherPlayerOrnaments,
                _ => false,
            };
        }

        return false;
    }

    private static uint GetLocalPlayerEntityId(GameObjectManager* manager)
    {
        var localPlayer = manager->Objects.IndexSorted[0].Value;
        return localPlayer != null && localPlayer->ObjectKind == ObjectKind.Pc ? localPlayer->EntityId : 0;
    }

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

            if (gameObject->NamePlateIconId != 0)
            {
                return false;
            }

            if ((gameObject->TargetableStatus & ObjectTargetableFlags.IsTargetable) == 0)
            {
                return true;
            }

            var handlers = stackalloc FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler*[MaxEventHandlers];
            var handlerCount = gameObject->GetEventHandlersImpl(handlers);
            if (handlerCount <= 0 || handlerCount > MaxEventHandlers)
            {
                return false;
            }

            var hasDefaultTalk = false;
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
                        hasDefaultTalk = true;
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

            return hasDefaultTalk;
        }
    }
}
