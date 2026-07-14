using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using EventHorizon.Localization;
using EventHorizon.Settings;

namespace EventHorizon.UI.Config;

internal partial class ConfigWindow
{
    #region Keep Rules
    private void DrawKeepRules()
    {
        var tableMinX = ImGui.GetCursorScreenPos().X;
        var tableMaxX = tableMinX + ImGui.GetContentRegionAvail().X;
        var tableSegment = 0;
        var rules = PlayerKeepRuleOrder.GetEffectiveOrder(configuration);
        var tableOpen = true;

        if (!BeginKeepRuleTable(tableSegment))
        {
            return;
        }

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            DrawRuleRow(rule, tableMinX, tableMaxX);

            if (rule != PlayerKeepRuleId.Race || !showRaceSexEditor)
            {
                continue;
            }

            ImGui.EndTable();
            tableOpen = false;
            DrawRaceFilterEditorInline();

            if (i < rules.Count - 1)
            {
                tableSegment++;
                if (!BeginKeepRuleTable(tableSegment))
                {
                    return;
                }

                tableOpen = true;
            }
        }

        if (tableOpen)
        {
            ImGui.EndTable();
        }

        DrawKeepRulesExplanation();

        if (draggedKeepRule.HasValue && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            draggedKeepRule = null;
            if (keepRuleOrderChanged)
            {
                keepRuleOrderChanged = false;
                SaveAndRefreshWithoutRuleReset();
            }
        }
    }

    private static bool BeginKeepRuleTable(int segment)
    {
        if (!ImGui.BeginTable($"###KeepRuleOrderTable{segment}", 5, ImGuiTableFlags.SizingStretchProp))
        {
            return false;
        }

        ImGui.TableSetupColumn("###KeepRuleOrderHandle", ImGuiTableColumnFlags.WidthFixed, 30f);
        ImGui.TableSetupColumn("###KeepRuleEnabled", ImGuiTableColumnFlags.WidthFixed, 36f);
        ImGui.TableSetupColumn("###KeepRuleName", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("###KeepRuleParameters", ImGuiTableColumnFlags.WidthFixed, 160f);
        ImGui.TableSetupColumn("###KeepRuleBudget", ImGuiTableColumnFlags.WidthFixed, 54f);

        return true;
    }

    private static void DrawKeepRulesExplanation()
    {
        ImGui.Spacing();
        DrawCardSeparator();
        ImGui.Spacing();
        ImGui.TextDisabled(Loc.Text("Config.KeepRules.ExplanationTitle"));
        DrawHelpText(Loc.Text("Config.KeepRules.Help"));
    }

    private void DrawRuleRow(PlayerKeepRuleId rule, float rowMinX, float rowMaxX)
    {
        var rowHeight = Math.Max(34f, ImGui.GetFrameHeight() + 10f);
        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

        ImGui.TableNextColumn();
        var rowY = ImGui.GetCursorScreenPos().Y;
        var rowMin = new Vector2(rowMinX, rowY);
        var rowMax = new Vector2(rowMaxX, rowY + rowHeight);
        var rowHovered = IsMouseInRect(rowMin, rowMax);
        var isDraggedRow = draggedKeepRule == rule;
        var isDropTarget = draggedKeepRule.HasValue && draggedKeepRule.Value != rule && rowHovered;
        DrawKeepRuleCellBackground(rowHovered, isDraggedRow, isDropTarget);

        CenterCursorYInRow(rowY, rowHeight, ImGui.GetFrameHeight());
        var handleState = DrawKeepRuleHandle(rule);

        ImGui.TableNextColumn();
        DrawKeepRuleCellBackground(rowHovered, isDraggedRow, isDropTarget);
        CenterCursorYInRow(rowY, rowHeight, ImGui.GetFrameHeight());
        DrawKeepRuleEnabledCheckbox(rule);

        ImGui.TableNextColumn();
        DrawKeepRuleCellBackground(rowHovered, isDraggedRow, isDropTarget);
        CenterCursorYInRow(rowY, rowHeight, ImGui.GetFrameHeight());
        DrawKeepRuleLabel(rule);

        ImGui.TableNextColumn();
        DrawKeepRuleCellBackground(rowHovered, isDraggedRow, isDropTarget);
        CenterCursorYInRow(rowY, rowHeight, ImGui.GetFrameHeight());
        DrawKeepRuleParameters(rule);

        ImGui.TableNextColumn();
        DrawKeepRuleCellBackground(rowHovered, isDraggedRow, isDropTarget);
        CenterCursorYInRow(rowY, rowHeight, ImGui.GetFrameHeight());
        DrawBudgetChip(rule);

        if (handleState.Active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            draggedKeepRule = rule;
        }

        if (draggedKeepRule.HasValue && draggedKeepRule.Value != rule && rowHovered)
        {
            MoveKeepRuleTo(draggedKeepRule.Value, rule);
        }

        DrawKeepRuleRowOutline(rowMin, rowMax, isDraggedRow, isDropTarget);
    }

    private static void DrawKeepRuleCellBackground(bool hovered, bool dragged, bool dropTarget)
    {
        var color =
            dropTarget ? ImGuiCol.HeaderActive
            : dragged ? ImGuiCol.ButtonActive
            : hovered ? ImGuiCol.HeaderHovered
            : ImGuiCol.TableRowBg;

        ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(color));
    }

    private static void DrawKeepRuleRowOutline(Vector2 min, Vector2 max, bool dragged, bool dropTarget)
    {
        if (!dragged && !dropTarget)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var color = ImGui.GetStyle().Colors[(int)(dropTarget ? ImGuiCol.Text : ImGuiCol.ButtonActive)];

        drawList.AddRect(min, max, ImGui.GetColorU32(color), 4f, ImDrawFlags.None, dropTarget ? 2f : 1.5f);
    }

    private static bool IsMouseInRect(Vector2 min, Vector2 max)
    {
        var mouse = ImGui.GetMousePos();
        return mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
    }

    private static ImGuiItemState DrawKeepRuleHandle(PlayerKeepRuleId rule)
    {
        var handleSize = new Vector2(22f, ImGui.GetFrameHeight());
        ImGui.InvisibleButton($"###KeepRuleHandle{rule}", handleSize);

        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var active = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (hovered || active)
        {
            ImGui.SetMouseCursor(active ? ImGuiMouseCursor.ResizeNs : ImGuiMouseCursor.Hand);
        }

        DrawHandleLines(min, max, hovered);

        return new ImGuiItemState(hovered, active);
    }

    private static void DrawHandleLines(Vector2 min, Vector2 max, bool hovered)
    {
        var drawList = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(hovered ? ImGuiCol.Text : ImGuiCol.TextDisabled);
        var width = 11f;
        var left = min.X + ((max.X - min.X - width) * 0.5f);
        var centerY = (min.Y + max.Y) * 0.5f;

        for (var i = -1; i <= 1; i++)
        {
            var y = centerY + (i * 4f);
            drawList.AddLine(new Vector2(left, y), new Vector2(left + width, y), color, 1.5f);
        }
    }

    private ImGuiItemState DrawKeepRuleEnabledCheckbox(PlayerKeepRuleId rule)
    {
        var enabled = IsKeepRuleEnabled(rule);
        if (ImGui.Checkbox($"###KeepRule{rule}", ref enabled))
        {
            SetKeepRuleEnabled(rule, enabled);
            SaveAndRefresh();
        }

        return new ImGuiItemState(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem));
    }

    private void DrawKeepRuleParameters(PlayerKeepRuleId rule)
    {
        switch (rule)
        {
            case PlayerKeepRuleId.Nearby:
                DrawNearbyPlayerOptions();
                break;
            case PlayerKeepRuleId.Race:
                if (ImGui.SmallButton(Loc.Text("Config.RaceFilter.Edit")))
                {
                    showRaceSexEditor = !showRaceSexEditor;
                }
                break;
            default:
                break;
        }
    }

    private static void DrawKeepRuleLabel(PlayerKeepRuleId rule)
    {
        ImGui.AlignTextToFramePadding();
        DrawAutoFitText(PlayerKeepRuleLabels.GetLabel(rule));

        var helpText = PlayerKeepRuleLabels.GetHelpText(rule);
        if (!string.IsNullOrEmpty(helpText))
        {
            DrawHelpMarker(helpText);
        }
    }

    private void DrawNearbyPlayerOptions()
    {
        ImGui.SetNextItemWidth(Math.Min(126f, ImGui.GetContentRegionAvail().X));
        var range = configuration.KeepNearbyPlayersRange;
        if (
            ImGui.SliderFloat(
                "###KeepNearbyPlayersRange",
                ref range,
                PlayerKeepRuleSettings.NearbyRangeMin,
                PlayerKeepRuleSettings.NearbyRangeMax,
                Loc.Text("Config.DistanceSliderFormat")
            )
        )
        {
            configuration.KeepNearbyPlayersRange = Math.Clamp(
                range,
                PlayerKeepRuleSettings.NearbyRangeMin,
                PlayerKeepRuleSettings.NearbyRangeMax
            );
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveAndRefresh();
        }
    }

    private bool IsKeepRuleEnabled(PlayerKeepRuleId rule) =>
        rule switch
        {
            PlayerKeepRuleId.TargetFocus => configuration.KeepTargetAndFocusPlayers,
            PlayerKeepRuleId.PartyAlliance => configuration.KeepPartyAndAllianceMembers,
            PlayerKeepRuleId.Friends => configuration.KeepFriends,
            PlayerKeepRuleId.TargetingMe => configuration.KeepPlayersTargetingMe,
            PlayerKeepRuleId.RecentChat => configuration.KeepRecentChatPlayers,
            PlayerKeepRuleId.Recruiting => configuration.KeepRecruitingPlayers,
            PlayerKeepRuleId.Nearby => configuration.KeepNearbyPlayers,
            PlayerKeepRuleId.Race => configuration.KeepSelectedRaces,
            _ => false,
        };

    private void SetKeepRuleEnabled(PlayerKeepRuleId rule, bool enabled)
    {
        switch (rule)
        {
            case PlayerKeepRuleId.TargetFocus:
                configuration.KeepTargetAndFocusPlayers = enabled;
                break;
            case PlayerKeepRuleId.PartyAlliance:
                configuration.KeepPartyAndAllianceMembers = enabled;
                break;
            case PlayerKeepRuleId.Friends:
                configuration.KeepFriends = enabled;
                break;
            case PlayerKeepRuleId.TargetingMe:
                configuration.KeepPlayersTargetingMe = enabled;
                break;
            case PlayerKeepRuleId.RecentChat:
                configuration.KeepRecentChatPlayers = enabled;
                break;
            case PlayerKeepRuleId.Recruiting:
                configuration.KeepRecruitingPlayers = enabled;
                break;
            case PlayerKeepRuleId.Nearby:
                configuration.KeepNearbyPlayers = enabled;
                break;
            case PlayerKeepRuleId.Race:
                configuration.KeepSelectedRaces = enabled;
                break;
        }
    }

    private void DrawBudgetChip(PlayerKeepRuleId ruleId)
    {
        var usesBudget = PlayerKeepRulePolicies.GetPolicy(configuration, ruleId) == PlayerKeepBudgetPolicy.Counted;

        var checkboxSize = ImGui.GetFrameHeight();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        if (availableWidth > checkboxSize)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((availableWidth - checkboxSize) * 0.5f));
        }

        if (ImGui.Checkbox($"###KeepRuleBudgetPolicy{ruleId}", ref usesBudget))
        {
            PlayerKeepRulePolicies.SetPolicy(
                configuration,
                ruleId,
                usesBudget ? PlayerKeepBudgetPolicy.Counted : PlayerKeepBudgetPolicy.Exempt
            );
            SaveAndRefresh();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(
                Loc.Text(usesBudget ? "Config.KeepRules.Budget.Counted.Tooltip" : "Config.KeepRules.Budget.Exempt.Tooltip")
            );
            ImGui.EndTooltip();
        }
    }

    private void DrawResetKeepRuleOrderButton(string label)
    {
        if (ImGui.SmallButton(label))
        {
            PlayerKeepRuleOrder.Reset(configuration);
            SaveAndRefreshWithoutRuleReset();
        }
    }

    private void MoveKeepRuleTo(PlayerKeepRuleId dragged, PlayerKeepRuleId target)
    {
        var order = new List<PlayerKeepRuleId>(PlayerKeepRuleOrder.GetEffectiveOrder(configuration));
        var from = order.IndexOf(dragged);
        var to = order.IndexOf(target);
        if (from < 0 || to < 0 || from == to)
        {
            return;
        }

        order.RemoveAt(from);
        order.Insert(Math.Min(to, order.Count), dragged);
        configuration.KeepRuleOrder = order;
        keepRuleOrderChanged = true;
    }

    private static void DrawHelpMarker(string text, bool sameLine = true)
    {
        if (sameLine)
        {
            ImGui.SameLine();
        }

        ImGuiComponents.HelpMarker(text, FontAwesomeIcon.InfoCircle);
    }

    #endregion
}
