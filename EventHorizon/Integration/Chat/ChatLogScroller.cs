using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.Integration.Chat;

internal sealed unsafe class ChatLogScroller(IGameGui gameGui, IFramework framework)
{
    private const string ChatLogAddonName = "ChatLog";
    private const string ChatLogPanelAddonNamePrefix = "ChatLogPanel_";

    public void ScrollActivePanelLines(int lineDelta)
    {
        if (lineDelta == 0)
        {
            return;
        }

        _ = framework.RunOnFrameworkThread(() => TryScrollActivePanelLines(lineDelta));
    }

    private bool TryScrollActivePanelLines(int lineDelta)
    {
        if (!TryGetActivePanel(out var panel))
        {
            return false;
        }

        var logViewer = &panel->LogViewer;
        if (logViewer->ChatText == null)
        {
            return false;
        }

        var wheelEvent = FindMouseWheelEvent(panel);
        if (wheelEvent == null || wheelEvent->Listener == null)
        {
            return false;
        }

        FireMouseWheelEvent(wheelEvent, (short)-lineDelta);
        return true;
    }

    private static void FireMouseWheelEvent(AtkEvent* registeredEvent, short wheelDirection)
    {
        var syntheticEvent = *registeredEvent;
        syntheticEvent.NextEvent = null;
        syntheticEvent.State = new AtkEventState { EventType = AtkEventType.MouseWheel, StateFlags = AtkEventStateFlags.None };

        var eventData = new AtkEventData();
        eventData.MouseData.WheelDirection = wheelDirection;

        registeredEvent->Listener->ReceiveEvent(AtkEventType.MouseWheel, (int)registeredEvent->Param, &syntheticEvent, &eventData);
    }

    private static AtkEvent* FindMouseWheelEvent(AddonChatLogPanel* panel)
    {
        var panelListener = (AtkEventListener*)panel;

        var result = FindEvent(&panel->AtkUnitBase.UldManager, AtkEventType.MouseWheel, panelListener);
        if (result != null)
        {
            return result;
        }

        if (panel->ChatComponent != null)
        {
            result = FindEvent(&panel->ChatComponent->UldManager, AtkEventType.MouseWheel, panelListener);
            if (result != null)
            {
                return result;
            }

            result = FindEvent(&panel->ChatComponent->UldManager, AtkEventType.MouseWheel, expectedListener: null);
            if (result != null)
            {
                return result;
            }
        }

        return FindEvent(&panel->AtkUnitBase.UldManager, AtkEventType.MouseWheel, expectedListener: null);
    }

    private static AtkEvent* FindEvent(AtkUldManager* uldManager, AtkEventType eventType, AtkEventListener* expectedListener)
    {
        if (uldManager == null || uldManager->NodeList == null)
        {
            return null;
        }

        for (var i = 0; i < uldManager->NodeListCount; i++)
        {
            var node = uldManager->NodeList[i];
            if (node == null)
            {
                continue;
            }

            for (var atkEvent = node->AtkEventManager.Event; atkEvent != null; atkEvent = atkEvent->NextEvent)
            {
                if (atkEvent->State.EventType != eventType)
                {
                    continue;
                }

                if (expectedListener != null && atkEvent->Listener != expectedListener)
                {
                    continue;
                }

                return atkEvent;
            }
        }

        return null;
    }

    private bool TryGetActivePanel(out AddonChatLogPanel* panel)
    {
        panel = null;

        var chatLogPointer = gameGui.GetAddonByName(ChatLogAddonName);
        if (chatLogPointer == nint.Zero)
        {
            return false;
        }

        var chatLog = (AddonChatLog*)chatLogPointer.Address;
        if (chatLog->AtkUnitBase.RootNode == null || chatLog->TabIndex >= chatLog->TabCount)
        {
            return false;
        }

        var panelPointer = gameGui.GetAddonByName($"{ChatLogPanelAddonNamePrefix}{chatLog->TabIndex}");
        if (panelPointer == nint.Zero)
        {
            return false;
        }

        panel = (AddonChatLogPanel*)panelPointer.Address;
        return panel->AtkUnitBase.RootNode != null && panel->LogViewer.ChatText != null;
    }
}
