using System;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class NonPlayerRules(Configuration configuration)
{
    public bool ShouldHide(GameObject* gameObject, GameObjectManager* manager)
    {
        var index = gameObject->ObjectIndex;
        var localPlayerEntityId = GetLocalPlayerEntityId(manager);
        if (CharacterObjectSlots.IsLocalReservedSlot(index))
        {
            return false;
        }

        if (index is >= EventNpcRule.FirstSlot and <= EventNpcRule.LastSlot)
        {
            return configuration.HideUnattachedEventNpcs && EventNpcRule.Evaluate(gameObject);
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

        public static bool Evaluate(GameObject* gameObject)
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
