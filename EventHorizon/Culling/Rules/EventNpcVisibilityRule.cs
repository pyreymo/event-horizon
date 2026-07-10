using System;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GameEventHandler = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler;

namespace EventHorizon.Culling.Rules;

internal sealed unsafe class EventNpcVisibilityRule
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
