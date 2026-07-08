using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace EventHorizon.Integration.Chat;

/// <summary>
/// STUB only
/// </summary>
internal sealed unsafe class ChatLogScroller(IGameGui gameGui, IFramework framework, IPluginLog log)
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

    public void JumpToMatchingLogMessage(string text, int direction)
    {
        if (string.IsNullOrWhiteSpace(text) || direction == 0)
        {
            return;
        }

        _ = framework.RunOnFrameworkThread(() => TryJumpToMatchingLogMessage(text, direction));
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

        var beforeFirstLine = logViewer->FirstLineVisible;
        var beforeLastLine = logViewer->LastLineVisible;
        var beforeMessagesAbove = logViewer->MessagesAboveCurrent;

        FireMouseWheelEvent(wheelEvent, ToWheelDirection(lineDelta));

        log.Information(
            "Chat scroll delta={RequestedDelta}: "
                + "first={BeforeFirst}->{AfterFirst}, "
                + "last={BeforeLast}->{AfterLast}, "
                + "messagesAbove={BeforeMessages}->{AfterMessages}, "
                + "totalLines={TotalLines}",
            lineDelta,
            beforeFirstLine,
            logViewer->FirstLineVisible,
            beforeLastLine,
            logViewer->LastLineVisible,
            beforeMessagesAbove,
            logViewer->MessagesAboveCurrent,
            logViewer->TotalLineCount
        );
        return true;
    }

    private bool TryJumpToMatchingLogMessage(string text, int direction)
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

        var matchingMessageIndices = FindMatchingLogMessageIndices(text);
        var targetMessageIndex =
            direction < 0
                ? FindPreviousMatchingIndex(matchingMessageIndices, (int)logViewer->FirstLineVisible - 1)
                : FindNextMatchingIndex(matchingMessageIndices, (int)logViewer->LastLineVisible + 1);
        if (targetMessageIndex < 0)
        {
            return false;
        }

        var lineDelta = targetMessageIndex - (int)logViewer->FirstLineVisible;
        if (lineDelta == 0)
        {
            return true;
        }

        FireMouseWheelEvent(wheelEvent, ToWheelDirection(lineDelta));
        return true;
    }

    private static int[] FindMatchingLogMessageIndices(string searchText)
    {
        var raptureLogModule = RaptureLogModule.Instance();
        if (raptureLogModule == null)
        {
            return [];
        }

        var messageCount = raptureLogModule->LogModule.LogMessageCount;
        if (messageCount <= 0)
        {
            return [];
        }

        var matchingMessageIndices = new List<int>();
        for (var i = 0; i < messageCount; i++)
        {
            if (!raptureLogModule->GetLogMessageDetail(i, out var senderBytes, out var messageBytes, out _, out _, out _, out _))
            {
                continue;
            }

            var senderText = ExtractText(senderBytes);
            var messageText = ExtractText(messageBytes);
            var combinedText = string.Concat(senderText, " ", messageText);
            if (ContainsText(combinedText, searchText))
            {
                matchingMessageIndices.Add(i);
            }
        }

        return matchingMessageIndices.ToArray();
    }

    private static string ExtractText(byte[] bytes)
    {
        var span = new ReadOnlySeStringSpan(bytes);
        return span.ExtractText();
    }

    private static int FindPreviousMatchingIndex(int[] indices, int startIndex)
    {
        for (var i = indices.Length - 1; i >= 0; i--)
        {
            if (indices[i] <= startIndex)
            {
                return indices[i];
            }
        }

        return -1;
    }

    private static int FindNextMatchingIndex(int[] indices, int startIndex)
    {
        for (var i = 0; i < indices.Length; i++)
        {
            if (indices[i] >= startIndex)
            {
                return indices[i];
            }
        }

        return -1;
    }

    private static bool ContainsText(string value, string text) => value.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static short ToWheelDirection(int lineDelta)
    {
        var wheelDirection = -lineDelta;
        return (short)Math.Clamp(wheelDirection, short.MinValue + 1, short.MaxValue);
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
