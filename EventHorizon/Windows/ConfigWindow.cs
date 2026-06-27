using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using EventHorizon.Localization;
using EventHorizon.ObjectTable;
using Lumina.Excel.Sheets;

namespace EventHorizon.Windows;

public class ConfigWindow : Window, IDisposable
{
    public enum Tab
    {
        Culling,
        Behavior,
    }

    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly IDataManager dataManager;
    private readonly PlayerPreviewRenderer playerPreviewRenderer = new();

    private Tab? pendingSelectedTab;
    private PlayerKeepRuleId? draggedKeepRule;
    private float cullingLeftColumnWidth = 690f;
    private long nextPlayerPreviewRefresh;
    private bool keepRuleOrderChanged;
    private bool playerPreviewEnabled = true;
    private bool showRaceSexEditor;

    private readonly record struct ImGuiItemState(bool Hovered, bool Active = false);

    #region Lifecycle

    public ConfigWindow(Plugin plugin, IDataManager dataManager)
        : base($"{Loc.Text("Config.Title")}###EventHorizonConfig")
    {
        Size = new Vector2(960, 780);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        this.dataManager = dataManager;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public void Open(Tab tab)
    {
        pendingSelectedTab = tab;
        IsOpen = true;
    }

    #endregion

    #region Draw

    public override void PreDraw()
    {
        WindowName = $"{Loc.Text("Config.Title")}###EventHorizonConfig";
    }

    public override void Draw()
    {
        DrawTabBar();

        pendingSelectedTab = null;
    }

    private void DrawTabBar()
    {
        if (!ImGui.BeginTabBar("###EventHorizonConfigTabs"))
        {
            return;
        }

        var tabToSelect = pendingSelectedTab;

        if (
            ImGui.BeginTabItem(
                $"{Loc.Text("Config.Tab.Culling")}###CullingTab",
                tabToSelect == Tab.Culling ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None
            )
        )
        {
            DrawCullingTab();
            ImGui.EndTabItem();
        }

        if (
            ImGui.BeginTabItem(
                $"{Loc.Text("Config.Tab.Behavior")}###BehaviorTab",
                tabToSelect == Tab.Behavior ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None
            )
        )
        {
            DrawBehaviorTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawCullingTab()
    {
        if (!configuration.HideAllOtherPlayers)
        {
            DrawStatusSummaryCard();
            return;
        }

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var splitterWidth = 8f;
        var rightPaddingWidth = 12f;
        var minLeftWidth = 420f;
        var minRightWidth = 260f;
        var maxLeftWidth = Math.Max(minLeftWidth, availableWidth - splitterWidth - rightPaddingWidth - minRightWidth);
        cullingLeftColumnWidth = Math.Clamp(cullingLeftColumnWidth, minLeftWidth, maxLeftWidth);

        if (!ImGui.BeginTable("###CullingContentColumns", 4, ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("###CullingMainColumn", ImGuiTableColumnFlags.WidthFixed, cullingLeftColumnWidth);
        ImGui.TableSetupColumn("###CullingColumnSplitter", ImGuiTableColumnFlags.WidthFixed, splitterWidth);
        ImGui.TableSetupColumn("###CullingInfoColumn", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("###CullingRightPaddingColumn", ImGuiTableColumnFlags.WidthFixed, rightPaddingWidth);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var contentStartY = ImGui.GetCursorScreenPos().Y;

        DrawCard(
            Loc.Text("Config.Section.HideTriggers"),
            () =>
            {
                DrawDutyRule();
                DrawLowPlayerCountRule();
            }
        );

        DrawCard(Loc.Text("Config.Section.VisiblePlayerBudget"), DrawVisiblePlayerLimitRule);
        DrawCard(Loc.Text("Config.Section.KeepRules"), DrawKeepRules, DrawResetKeepRuleOrderButton);

        DrawCard(
            Loc.Text("Config.Section.AttachedObjects"),
            () =>
            {
                DrawHelpText(Loc.Text("Config.AttachedObjects.Help"));
                ImGui.Spacing();
                DrawOtherPlayerCompanionRule();
                DrawOtherPlayerOrnamentRule();
            }
        );
        var leftContentEndY = ImGui.GetCursorScreenPos().Y;

        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        DrawRightPanel();
        var rightContentEndY = ImGui.GetCursorScreenPos().Y;
        ImGui.TableNextColumn();

        var splitterHeight = Math.Max(160f, Math.Max(leftContentEndY, rightContentEndY) - contentStartY);
        ImGui.TableSetColumnIndex(1);
        DrawCullingColumnSplitter(splitterHeight, minLeftWidth, maxLeftWidth);

        ImGui.EndTable();
    }

    private void DrawCullingColumnSplitter(float height, float minLeftWidth, float maxLeftWidth)
    {
        ImGui.InvisibleButton("###CullingColumnSplitterHandle", new Vector2(8f, height));

        if (ImGui.IsItemActive())
        {
            cullingLeftColumnWidth = Math.Clamp(cullingLeftColumnWidth + ImGui.GetIO().MouseDelta.X, minLeftWidth, maxLeftWidth);
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var x = (min.X + max.X) * 0.5f;
        var color = ImGui.GetColorU32(ImGui.IsItemHovered() || ImGui.IsItemActive() ? ImGuiCol.SeparatorHovered : ImGuiCol.Separator);
        ImGui.GetWindowDrawList().AddLine(new Vector2(x, min.Y), new Vector2(x, max.Y), color, 1.5f);
    }

    private void DrawRightPanel()
    {
        DrawStatusSummaryCard();
        DrawPlayerPreview();
    }

    private void DrawPlayerPreview()
    {
        DrawCard(
            Loc.Text("Config.Preview.Title"),
            () =>
            {
                if (!playerPreviewEnabled)
                {
                    DrawHelpText(Loc.Text("Config.Preview.Disabled"));
                    return;
                }

                RefreshPlayerPreviewIfNeeded();
                var side = Math.Max(
                    PlayerPreviewConstants.MinimumRange,
                    ImGui.GetContentRegionAvail().X - PlayerPreviewConstants.CardContentRightPadding
                );
                playerPreviewRenderer.Draw(plugin.PlayerPreviewSnapshot, side, GetKeepRuleLabel);
                AddVerticalSpace(4f);
                DrawHelpText(Loc.Text("Config.Preview.PerformanceNote"));
            },
            DrawPlayerPreviewToggle
        );
    }

    private void DrawPlayerPreviewToggle()
    {
        ImGui.Checkbox(Loc.Text("Config.Preview.Toggle"), ref playerPreviewEnabled);
    }

    private void RefreshPlayerPreviewIfNeeded()
    {
        var now = Environment.TickCount64;
        if (now < nextPlayerPreviewRefresh)
        {
            return;
        }

        plugin.RefreshPlayerPreview();
        nextPlayerPreviewRefresh = now + PlayerPreviewConstants.FastRefreshIntervalMs;
    }

    private void DrawBehaviorTab()
    {
        DrawCard(
            Loc.Text("Config.Tab.Behavior"),
            () =>
            {
                var showDtrBar = configuration.ShowDtrBar;
                if (ImGui.Checkbox(Loc.Text("Config.ShowDtrBar"), ref showDtrBar))
                {
                    configuration.ShowDtrBar = showDtrBar;
                    SaveAndRefreshDtrBar();
                }

                var enableFadeTransitions = configuration.EnableFadeTransitions;
                if (ImGui.Checkbox(Loc.Text("Config.EnableFadeTransitions"), ref enableFadeTransitions))
                {
                    configuration.EnableFadeTransitions = enableFadeTransitions;
                    SaveAndRefresh();
                }
            }
        );
    }

    #endregion

    #region Layout Helpers

    private static void AddVerticalSpace(float height)
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + height);
    }

    private static void CenterCursorYInRow(float rowY, float rowHeight, float itemHeight)
    {
        var cursor = ImGui.GetCursorScreenPos();
        var centeredY = rowY + Math.Max(0f, (rowHeight - itemHeight) * 0.5f);
        ImGui.SetCursorScreenPos(new Vector2(cursor.X, centeredY));
    }

    private static void DrawCard(string title, System.Action content, System.Action? headerAction = null)
    {
        AddVerticalSpace(8f);
        DrawFramedCard(
            $"###Card{title}",
            () =>
            {
                DrawCardHeader(title, headerAction);
                DrawCardSeparator();
                content();
            }
        );
    }

    private static void DrawCardHeader(string title, System.Action? headerAction)
    {
        if (headerAction is null)
        {
            ImGui.TextUnformatted(title);
            return;
        }

        if (!ImGui.BeginTable($"###CardHeader{title}", 3, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TextUnformatted(title);
            ImGui.SameLine();
            headerAction();
            return;
        }

        ImGui.TableSetupColumn("###CardHeaderTitle", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("###CardHeaderAction", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("###CardHeaderPadding", ImGuiTableColumnFlags.WidthFixed, 12f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(title);
        ImGui.TableNextColumn();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - 142f));
        headerAction();
        ImGui.TableNextColumn();
        ImGui.EndTable();
    }

    private static void DrawFramedCard(string id, System.Action content)
    {
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = Math.Max(1f, ImGui.GetContentRegionAvail().X - 6f);
        var padding = new Vector2(12f, 10f);
        var rightPadding = 18f;

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.PushID(id);
        ImGui.SetCursorScreenPos(start + padding);
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(start.X + width - rightPadding);
        content();
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        ImGui.PopID();

        var contentMax = ImGui.GetItemRectMax();
        var height = Math.Max(ImGui.GetTextLineHeightWithSpacing() + (padding.Y * 2f), contentMax.Y - start.Y + padding.Y);
        var end = new Vector2(start.X + width, start.Y + height);

        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(ImGuiCol.ChildBg), 6f);
        drawList.AddRect(start, end, ImGui.GetColorU32(ImGuiCol.Border), 6f);
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
    }

    private static void DrawCardSeparator()
    {
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = Math.Max(1f, ImGui.GetContentRegionAvail().X - 18f);
        var gapHeight = 9f;
        var y = start.Y + (gapHeight * 0.5f);
        drawList.AddLine(new Vector2(start.X, y), new Vector2(start.X + width, y), ImGui.GetColorU32(ImGuiCol.Separator));
        ImGui.Dummy(new Vector2(width, gapHeight));
    }

    private static void DrawHelpText(string text)
    {
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }

    #endregion

    #region Keep Rules

    private void DrawDutyRule()
    {
        var disableInDuty = configuration.DisableInDuty;
        if (ImGui.Checkbox(Loc.Text("Config.DisableInDuty"), ref disableInDuty))
        {
            configuration.DisableInDuty = disableInDuty;
            SaveAndRefresh();
        }
    }

    private void DrawStatusOverview()
    {
        DrawStatusSummaryCard();
    }

    private void DrawStatusSummaryCard()
    {
        var currentOtherPlayerCount = ObjectTableStats.CurrentOtherPlayerCount();
        var hiddenPlayerCount = plugin.HiddenPlayerCount;
        var keptOtherPlayerCount = Math.Max(0, currentOtherPlayerCount - hiddenPlayerCount);
        var suspensionReason = GetCullingSuspensionReason(currentOtherPlayerCount);

        DrawCard(
            Loc.Text("Config.StatusSummary.Title"),
            () =>
            {
                DrawPlayerHidingMasterSwitch();
                ImGui.Spacing();

                if (!string.IsNullOrEmpty(suspensionReason))
                {
                    DrawSummaryRow(
                        Loc.Text("Config.StatusSummary.State"),
                        string.Format(Loc.Text("Config.StatusPaused.Compact"), suspensionReason)
                    );
                }
                else
                {
                    DrawSummaryRow(Loc.Text("Config.StatusSummary.State"), Loc.Text("Config.StatusSummary.Running"));
                }

                DrawSummaryRow(
                    Loc.Text("Config.StatusSummary.VisibleHidden"),
                    string.Format(Loc.Text("Config.StatusSummary.VisibleHidden.Value"), keptOtherPlayerCount, hiddenPlayerCount)
                );

                DrawSummaryRow(Loc.Text("Config.StatusSummary.BudgetLimit"), GetBudgetLimitSummary());
            }
        );
    }

    private void DrawPlayerHidingMasterSwitch()
    {
        var statusText = configuration.HideAllOtherPlayers
            ? Loc.Text("Config.MasterSwitch.Enabled")
            : Loc.Text("Config.MasterSwitch.Disabled");
        var label = $"{Loc.Text("Config.HideAllOtherPlayers.Short")} · {statusText}###PlayerHidingMasterSwitch";
        var width = Math.Max(1f, ImGui.GetContentRegionAvail().X - 18f);
        var pushedColors = false;

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8f, 6f));
        if (configuration.HideAllOtherPlayers)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
            pushedColors = true;
        }

        if (ImGui.Button(label, new Vector2(width, 0f)))
        {
            configuration.HideAllOtherPlayers = !configuration.HideAllOtherPlayers;
            SaveAndRefresh();
        }

        if (pushedColors)
        {
            ImGui.PopStyleColor();
        }

        ImGui.PopStyleVar();
    }

    private static void DrawSummaryRow(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
    }

    private string GetBudgetLimitSummary()
    {
        if (!configuration.LimitVisiblePlayerCount)
        {
            return Loc.Text("Config.StatusSummary.BudgetLimit.Disabled");
        }

        return string.Format(Loc.Text("Config.StatusSummary.BudgetLimit.Value"), configuration.VisiblePlayerCountLimit);
    }

    private string GetCullingSuspensionReason(int currentOtherPlayerCount)
    {
        if (plugin.IsDutyCullingSuspended)
        {
            return Loc.Text("Config.DutyPauseReason");
        }

        if (IsLowPlayerCountCullingSuspended(currentOtherPlayerCount))
        {
            return string.Format(
                Loc.Text("Config.LowPlayerCountPauseReason"),
                currentOtherPlayerCount,
                configuration.DisableCullingPlayerCountThreshold
            );
        }

        return string.Empty;
    }

    private bool IsLowPlayerCountCullingSuspended(int currentOtherPlayerCount)
    {
        return configuration.HideAllOtherPlayers
            && configuration.DisableCullingBelowPlayerCount
            && currentOtherPlayerCount < configuration.DisableCullingPlayerCountThreshold;
    }

    private void DrawLowPlayerCountRule()
    {
        var disableCullingBelowPlayerCount = configuration.DisableCullingBelowPlayerCount;
        if (ImGui.Checkbox(Loc.Text("Config.DisableCullingBelowPlayerCount"), ref disableCullingBelowPlayerCount))
        {
            configuration.DisableCullingBelowPlayerCount = disableCullingBelowPlayerCount;
            SaveAndRefresh();
        }

        if (!configuration.DisableCullingBelowPlayerCount)
        {
            return;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        var threshold = configuration.DisableCullingPlayerCountThreshold;
        if (ImGui.SliderInt("###DisableCullingPlayerCountThreshold", ref threshold, 1, 100))
        {
            configuration.DisableCullingPlayerCountThreshold = Math.Clamp(threshold, 1, 100);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveAndRefresh();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Text("Config.DisableCullingPlayerCountThresholdSuffix"));
    }

    private void DrawVisiblePlayerLimitRule()
    {
        if (!ImGui.BeginTable("###VisiblePlayerLimitRule", 3, ImGuiTableFlags.SizingStretchProp))
        {
            DrawVisiblePlayerLimitRuleFallback();
            return;
        }

        ImGui.TableSetupColumn("###VisibleLimitEnabled", ImGuiTableColumnFlags.WidthFixed, 180f);
        ImGui.TableSetupColumn("###VisibleLimitSlider", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("###VisibleLimitPadding", ImGuiTableColumnFlags.WidthFixed, 12f);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        var limitVisiblePlayerCount = configuration.LimitVisiblePlayerCount;
        if (ImGui.Checkbox(Loc.Text("Config.LimitVisiblePlayerCount"), ref limitVisiblePlayerCount))
        {
            configuration.LimitVisiblePlayerCount = limitVisiblePlayerCount;
            SaveAndRefresh();
        }

        ImGui.TableNextColumn();
        DrawVisiblePlayerLimitSlider(Math.Max(1f, ImGui.GetContentRegionAvail().X - 6f));
        ImGui.TableNextColumn();

        ImGui.EndTable();

        AddVerticalSpace(4f);
        DrawHelpText(Loc.Text("Config.LimitVisiblePlayerCount.Help"));
    }

    private void DrawVisiblePlayerLimitRuleFallback()
    {
        var limitVisiblePlayerCount = configuration.LimitVisiblePlayerCount;
        if (ImGui.Checkbox(Loc.Text("Config.LimitVisiblePlayerCount"), ref limitVisiblePlayerCount))
        {
            configuration.LimitVisiblePlayerCount = limitVisiblePlayerCount;
            SaveAndRefresh();
        }

        ImGui.SameLine();
        DrawVisiblePlayerLimitSlider(Math.Max(120f, ImGui.GetContentRegionAvail().X - 12f));

        AddVerticalSpace(4f);
        DrawHelpText(Loc.Text("Config.LimitVisiblePlayerCount.Help"));
    }

    private void DrawVisiblePlayerLimitSlider(float width)
    {
        if (!configuration.LimitVisiblePlayerCount)
        {
            ImGui.BeginDisabled();
        }

        ImGui.SetNextItemWidth(width);
        var limit = configuration.VisiblePlayerCountLimit;
        if (ImGui.SliderInt("###VisiblePlayerCountLimit", ref limit, 1, 100))
        {
            configuration.VisiblePlayerCountLimit = Math.Clamp(limit, 1, 100);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveAndRefresh();
        }

        if (!configuration.LimitVisiblePlayerCount)
        {
            ImGui.EndDisabled();
        }
    }

    private void DrawOtherPlayerCompanionRule()
    {
        var hideOtherPlayerCompanions = configuration.HideOtherPlayerCompanions;
        if (ImGui.Checkbox(Loc.Text("Config.HideOtherPlayerCompanions"), ref hideOtherPlayerCompanions))
        {
            configuration.HideOtherPlayerCompanions = hideOtherPlayerCompanions;
            SaveAndRefresh();
        }
    }

    private void DrawOtherPlayerOrnamentRule()
    {
        var hideOtherPlayerOrnaments = configuration.HideOtherPlayerOrnaments;
        if (ImGui.Checkbox(Loc.Text("Config.HideOtherPlayerOrnaments"), ref hideOtherPlayerOrnaments))
        {
            configuration.HideOtherPlayerOrnaments = hideOtherPlayerOrnaments;
            SaveAndRefresh();
        }
    }

    private void DrawKeepRules()
    {
        var tableMinX = ImGui.GetCursorScreenPos().X;
        var tableMaxX = tableMinX + ImGui.GetContentRegionAvail().X - 12f;

        if (!ImGui.BeginTable("###KeepRuleOrderTable", 6, ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("###KeepRuleOrderHandle", ImGuiTableColumnFlags.WidthFixed, 30f);
        ImGui.TableSetupColumn("###KeepRuleEnabled", ImGuiTableColumnFlags.WidthFixed, 36f);
        ImGui.TableSetupColumn("###KeepRuleName", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("###KeepRuleParameters", ImGuiTableColumnFlags.WidthFixed, 160f);
        ImGui.TableSetupColumn("###KeepRuleBudget", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("###KeepRulePadding", ImGuiTableColumnFlags.WidthFixed, 12f);

        foreach (var rule in PlayerKeepRuleOrder.GetEffectiveOrder(configuration))
        {
            DrawRuleRow(rule, tableMinX, tableMaxX);
        }

        ImGui.EndTable();

        if (showRaceSexEditor)
        {
            ImGui.Indent();
            DrawRaceFilterEditor();
            ImGui.Unindent();
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
        DrawKeepRuleCellBackground(rowHovered);

        CenterCursorYInRow(rowY, rowHeight, ImGui.GetFrameHeight());
        var handleState = DrawKeepRuleHandle(rule);

        ImGui.TableNextColumn();
        DrawKeepRuleCellBackground(rowHovered);
        CenterCursorYInRow(rowY, rowHeight, ImGui.GetFrameHeight());
        DrawKeepRuleEnabledCheckbox(rule);

        ImGui.TableNextColumn();
        DrawKeepRuleCellBackground(rowHovered);
        CenterCursorYInRow(rowY, rowHeight, ImGui.GetFrameHeight());
        DrawKeepRuleLabel(rule);

        ImGui.TableNextColumn();
        DrawKeepRuleCellBackground(rowHovered);
        CenterCursorYInRow(rowY, rowHeight, ImGui.GetFrameHeight());
        DrawKeepRuleParameters(rule);

        ImGui.TableNextColumn();
        DrawKeepRuleCellBackground(rowHovered);
        CenterCursorYInRow(rowY, rowHeight, ImGui.GetFrameHeight());
        DrawBudgetChip(rule);

        ImGui.TableNextColumn();

        if (handleState.Active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            draggedKeepRule = rule;
        }

        if (draggedKeepRule.HasValue && draggedKeepRule.Value != rule && rowHovered)
        {
            MoveKeepRuleTo(draggedKeepRule.Value, rule);
        }
    }

    private static void DrawKeepRuleCellBackground(bool hovered)
    {
        ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(hovered ? ImGuiCol.HeaderHovered : ImGuiCol.TableRowBg));
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
        DrawHandleLines(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), hovered || active);

        return new ImGuiItemState(hovered, active);
    }

    private static void DrawHandleLines(Vector2 min, Vector2 max, bool highlighted)
    {
        var drawList = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(highlighted ? ImGuiCol.Text : ImGuiCol.TextDisabled);
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
        ImGui.TextUnformatted(GetKeepRuleLabel(rule));

        var helpText = GetKeepRuleHelpText(rule);
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
                PlayerPreviewConstants.NearbyRangeMin,
                PlayerPreviewConstants.NearbyRangeMax,
                Loc.Text("Config.DistanceSliderFormat")
            )
        )
        {
            configuration.KeepNearbyPlayersRange = Math.Clamp(
                range,
                PlayerPreviewConstants.NearbyRangeMin,
                PlayerPreviewConstants.NearbyRangeMax
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
        var usesBudget = PlayerKeepRuleBudgetDefaults.GetPolicy(configuration, ruleId) == PlayerKeepBudgetPolicy.Counted;

        var checkboxSize = ImGui.GetFrameHeight();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        if (availableWidth > checkboxSize)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((availableWidth - checkboxSize) * 0.5f));
        }

        if (ImGui.Checkbox($"###KeepRuleBudgetPolicy{ruleId}", ref usesBudget))
        {
            PlayerKeepRuleBudgetDefaults.SetPolicy(
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

    private void DrawResetKeepRuleOrderButton()
    {
        if (ImGui.SmallButton(Loc.Text("Config.KeepRuleOrder.Reset")))
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

    private static string GetKeepRuleLabel(PlayerKeepRuleId rule) =>
        rule switch
        {
            PlayerKeepRuleId.TargetFocus => Loc.Text("Config.KeepTargetAndFocusPlayers"),
            PlayerKeepRuleId.PartyAlliance => Loc.Text("Config.KeepPartyAndAllianceMembers"),
            PlayerKeepRuleId.Friends => Loc.Text("Config.KeepFriends"),
            PlayerKeepRuleId.TargetingMe => Loc.Text("Config.KeepPlayersTargetingMe"),
            PlayerKeepRuleId.RecentChat => Loc.Text("Config.KeepRecentChatPlayers"),
            PlayerKeepRuleId.Recruiting => Loc.Text("Config.KeepRecruitingPlayers"),
            PlayerKeepRuleId.Nearby => Loc.Text("Config.KeepNearbyPlayers"),
            PlayerKeepRuleId.Race => Loc.Text("Config.KeepRaceFilter"),
            _ => rule.ToString(),
        };

    private static string GetKeepRuleHelpText(PlayerKeepRuleId rule) =>
        rule switch
        {
            PlayerKeepRuleId.TargetFocus => Loc.Text("Config.KeepTargetAndFocusPlayers.Help"),
            PlayerKeepRuleId.TargetingMe => Loc.Text("Config.KeepPlayersTargetingMe.Help"),
            PlayerKeepRuleId.RecentChat => Loc.Text("Config.KeepRecentChatPlayers.Help"),
            _ => string.Empty,
        };

    private static void DrawHelpMarker(string text, bool sameLine = true)
    {
        if (sameLine)
        {
            ImGui.SameLine();
        }

        ImGuiComponents.HelpMarker(text, FontAwesomeIcon.InfoCircle);
    }

    #endregion

    #region Race/Sex Filter

    private void DrawRaceFilterEditor()
    {
        if (ImGui.SmallButton(Loc.Text("Config.RaceFilter.SelectAll")))
        {
            SetAllRaceSexFilters(true);
            SaveAndRefresh();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.Text("Config.RaceFilter.Clear")))
        {
            configuration.KeptRaceSex.Clear();
            SaveAndRefresh();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.Text("Config.RaceFilter.Invert")))
        {
            InvertRaceSexFilters();
            SaveAndRefresh();
        }

        if (
            !ImGui.BeginTable(
                "###RaceSexFilterTable",
                4,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV
            )
        )
        {
            return;
        }

        ImGui.TableSetupColumn(Loc.Text("Config.RaceFilter.Race"));
        ImGui.TableSetupColumn(Loc.Text("Config.RaceFilter.Male"));
        ImGui.TableSetupColumn(Loc.Text("Config.RaceFilter.Female"));
        ImGui.TableSetupColumn("###RaceFilterPadding", ImGuiTableColumnFlags.WidthFixed, 12f);
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Loc.Text("Config.RaceFilter.Race"));
        ImGui.TableNextColumn();
        DrawSexColumnHeader(RaceSexFilter.MaleSex, Loc.Text("Config.RaceFilter.Male"));
        ImGui.TableNextColumn();
        DrawSexColumnHeader(RaceSexFilter.FemaleSex, Loc.Text("Config.RaceFilter.Female"));
        ImGui.TableNextColumn();

        for (var race = RaceSexFilter.MinRace; race <= RaceSexFilter.MaxRace; race++)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawRaceRowHeader(race);

            DrawRaceSexFilterCell(race, RaceSexFilter.MaleSex);
            DrawRaceSexFilterCell(race, RaceSexFilter.FemaleSex);
            ImGui.TableNextColumn();
        }

        ImGui.EndTable();
    }

    private void DrawRaceRowHeader(byte race)
    {
        if (ImGui.Selectable($"{GetRaceName(race)}###RaceFilterRace{race}"))
        {
            ToggleRace(race);
            SaveAndRefresh();
        }
    }

    private void DrawSexColumnHeader(byte sex, string label)
    {
        if (ImGui.Selectable($"{label}###RaceFilterSex{sex}"))
        {
            ToggleSex(sex);
            SaveAndRefresh();
        }
    }

    private void DrawRaceSexFilterCell(byte race, byte sex)
    {
        ImGui.TableNextColumn();

        var value = RaceSexFilter.Pack(race, sex);
        var selected = configuration.KeptRaceSex.Contains(value);
        if (!ImGui.Checkbox($"###RaceSexFilter{race}_{sex}", ref selected))
        {
            return;
        }

        if (selected)
        {
            configuration.KeptRaceSex.Add(value);
        }
        else
        {
            configuration.KeptRaceSex.Remove(value);
        }

        SaveAndRefresh();
    }

    private void SetAllRaceSexFilters(bool selected)
    {
        configuration.KeptRaceSex.Clear();
        if (!selected)
        {
            return;
        }

        for (var race = RaceSexFilter.MinRace; race <= RaceSexFilter.MaxRace; race++)
        {
            configuration.KeptRaceSex.Add(RaceSexFilter.Pack(race, RaceSexFilter.MaleSex));
            configuration.KeptRaceSex.Add(RaceSexFilter.Pack(race, RaceSexFilter.FemaleSex));
        }
    }

    private void InvertRaceSexFilters()
    {
        for (var race = RaceSexFilter.MinRace; race <= RaceSexFilter.MaxRace; race++)
        {
            ToggleRaceSexFilter(race, RaceSexFilter.MaleSex);
            ToggleRaceSexFilter(race, RaceSexFilter.FemaleSex);
        }
    }

    private void ToggleRaceSexFilter(byte race, byte sex)
    {
        var value = RaceSexFilter.Pack(race, sex);
        if (!configuration.KeptRaceSex.Remove(value))
        {
            configuration.KeptRaceSex.Add(value);
        }
    }

    private void ToggleRace(byte race)
    {
        var allSelected =
            configuration.KeptRaceSex.Contains(RaceSexFilter.Pack(race, RaceSexFilter.MaleSex))
            && configuration.KeptRaceSex.Contains(RaceSexFilter.Pack(race, RaceSexFilter.FemaleSex));

        SetRaceSexFilter(race, RaceSexFilter.MaleSex, !allSelected);
        SetRaceSexFilter(race, RaceSexFilter.FemaleSex, !allSelected);
    }

    private void ToggleSex(byte sex)
    {
        var allSelected = true;
        for (var race = RaceSexFilter.MinRace; race <= RaceSexFilter.MaxRace; race++)
        {
            allSelected &= configuration.KeptRaceSex.Contains(RaceSexFilter.Pack(race, sex));
        }

        for (var race = RaceSexFilter.MinRace; race <= RaceSexFilter.MaxRace; race++)
        {
            SetRaceSexFilter(race, sex, !allSelected);
        }
    }

    private void SetRaceSexFilter(byte race, byte sex, bool selected)
    {
        var value = RaceSexFilter.Pack(race, sex);
        if (selected)
        {
            configuration.KeptRaceSex.Add(value);
        }
        else
        {
            configuration.KeptRaceSex.Remove(value);
        }
    }

    #endregion

    #region Persistence

    private void SaveAndRefresh()
    {
        configuration.Save();
        plugin.RefreshObjectCulling(resetRuleState: true);
        plugin.RefreshDtrBar();
    }

    private void SaveAndRefreshWithoutRuleReset()
    {
        configuration.Save();
        plugin.RefreshObjectCulling();
        plugin.RefreshDtrBar();
    }

    private void SaveAndRefreshDtrBar()
    {
        configuration.Save();
        plugin.RefreshDtrBar();
    }

    #endregion

    #region Data

    private string GetRaceName(byte race)
    {
        if (dataManager.GetExcelSheet<Race>().TryGetRow(race, out var row))
        {
            return row.Masculine.ToString();
        }

        return Loc.Text("Config.Race.Unknown");
    }

    #endregion
}
