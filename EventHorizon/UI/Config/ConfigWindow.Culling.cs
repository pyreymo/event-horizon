using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using EventHorizon.Culling;
using EventHorizon.Localization;

namespace EventHorizon.UI.Config;

public partial class ConfigWindow
{
    #region Culling Tab
    private void DrawCullingTab()
    {
        if (!configuration.HideAllOtherPlayers)
        {
            DrawStatusSummaryCard();
            return;
        }

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var splitterWidth = 8f;
        var minLeftWidth = 420f;
        var minRightWidth = 260f;
        var maxLeftWidth = Math.Max(minLeftWidth, availableWidth - splitterWidth - minRightWidth);
        cullingLeftColumnWidth = Math.Clamp(cullingLeftColumnWidth, minLeftWidth, maxLeftWidth);

        if (!ImGui.BeginTable("###CullingContentColumns", 3, ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("###CullingMainColumn", ImGuiTableColumnFlags.WidthFixed, cullingLeftColumnWidth);
        ImGui.TableSetupColumn("###CullingColumnSplitter", ImGuiTableColumnFlags.WidthFixed, splitterWidth);
        ImGui.TableSetupColumn("###CullingInfoColumn", ImGuiTableColumnFlags.WidthStretch);
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
                DrawOtherPlayerCompanionRule();
                DrawOtherPlayerOrnamentRule();
            }
        );
        var leftContentEndY = ImGui.GetCursorScreenPos().Y;

        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        DrawRightPanel();
        var rightContentEndY = ImGui.GetCursorScreenPos().Y;

        var splitterHeight = Math.Max(160f, Math.Max(leftContentEndY, rightContentEndY) - contentStartY);
        ImGui.TableSetColumnIndex(1);
        DrawCullingColumnSplitter(splitterHeight, minLeftWidth, maxLeftWidth);

        ImGui.EndTable();
    }

    private void DrawCullingColumnSplitter(float height, float minLeftWidth, float maxLeftWidth)
    {
        ImGui.InvisibleButton("###CullingColumnSplitterHandle", new Vector2(8f, height));

        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        }

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
        var title = Loc.Text("Config.Preview.Title");
        if (isPlayerPreviewWindowOpen())
        {
            DrawCollapsedPlayerPreview(title);
            return;
        }

        DrawCard(
            title,
            () => playerPreviewPanel.DrawInlineContent(PlayerKeepRuleText.GetLabel),
            DrawPlayerPreviewActions,
            ImGui.GetFrameHeight()
        );
    }

    private void DrawCollapsedPlayerPreview(string title)
    {
        AddVerticalSpace(8f);
        DrawFramedCard($"###Card{title}", () => DrawCardHeader(title, DrawPlayerPreviewActions, ImGui.GetFrameHeight()));
    }

    private void DrawPlayerPreviewActions()
    {
        var previewWindowOpen = isPlayerPreviewWindowOpen();
        if (previewWindowOpen)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
        }

        if (ImGuiComponents.IconButton("###PlayerPreviewPopOut", FontAwesomeIcon.ArrowUpRightFromSquare))
        {
            togglePlayerPreviewWindow();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.Text("Config.Preview.PopOut"));
        }

        if (previewWindowOpen)
        {
            ImGui.PopStyleColor();
        }
    }

    #endregion

    #region Culling Settings
    private void DrawDutyRule()
    {
        var disableInDuty = configuration.DisableInDuty;
        if (DrawAutoFitCheckbox("DisableInDuty", Loc.Text("Config.DisableInDuty"), ref disableInDuty))
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

                DrawSummaryRow(Loc.Text("Config.StatusSummary.Fps"), GetFrameRateSummary());

                DrawSummaryRow(
                    Loc.Text("Config.StatusSummary.VisibleHidden"),
                    string.Format(Loc.Text("Config.StatusSummary.VisibleHidden.Value"), keptOtherPlayerCount, hiddenPlayerCount)
                );
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

    private static unsafe string GetFrameRateSummary()
    {
        var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        var frameRate = framework != null ? framework->FrameRate : 0f;
        return string.Format(Loc.Text("Config.StatusSummary.Fps.Value"), frameRate);
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
        if (
            DrawAutoFitCheckbox(
                "DisableCullingBelowPlayerCount",
                Loc.Text("Config.DisableCullingBelowPlayerCount"),
                ref disableCullingBelowPlayerCount
            )
        )
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
        DrawAutoFitText(Loc.Text("Config.DisableCullingPlayerCountThresholdSuffix"));
    }

    private void DrawVisiblePlayerLimitRule()
    {
        var label = Loc.Text("Config.LimitVisiblePlayerCount");
        if (!ImGui.BeginTable("###VisiblePlayerLimitRule", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
        {
            DrawVisiblePlayerLimitRuleFallback();
            return;
        }

        ImGui.TableSetupColumn("###VisibleLimitEnabled", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("###VisibleLimitSlider", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        var limitVisiblePlayerCount = configuration.LimitVisiblePlayerCount;
        if (DrawAutoFitCheckbox("LimitVisiblePlayerCount", label, ref limitVisiblePlayerCount))
        {
            configuration.LimitVisiblePlayerCount = limitVisiblePlayerCount;
            SaveAndRefresh();
        }

        ImGui.TableNextColumn();
        DrawVisiblePlayerLimitSlider(Math.Max(1f, ImGui.GetContentRegionAvail().X - 6f));

        ImGui.EndTable();

        AddVerticalSpace(4f);
        DrawHelpText(Loc.Text("Config.LimitVisiblePlayerCount.Help"));
    }

    private void DrawVisiblePlayerLimitRuleFallback()
    {
        var limitVisiblePlayerCount = configuration.LimitVisiblePlayerCount;
        if (DrawAutoFitCheckbox("LimitVisiblePlayerCountFallback", Loc.Text("Config.LimitVisiblePlayerCount"), ref limitVisiblePlayerCount))
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
        if (DrawAutoFitCheckbox("HideOtherPlayerCompanions", Loc.Text("Config.HideOtherPlayerCompanions"), ref hideOtherPlayerCompanions))
        {
            configuration.HideOtherPlayerCompanions = hideOtherPlayerCompanions;
            SaveAndRefresh();
        }
    }

    private void DrawOtherPlayerOrnamentRule()
    {
        var hideOtherPlayerOrnaments = configuration.HideOtherPlayerOrnaments;
        if (DrawAutoFitCheckbox("HideOtherPlayerOrnaments", Loc.Text("Config.HideOtherPlayerOrnaments"), ref hideOtherPlayerOrnaments))
        {
            configuration.HideOtherPlayerOrnaments = hideOtherPlayerOrnaments;
            SaveAndRefresh();
        }
    }

    #endregion
}
