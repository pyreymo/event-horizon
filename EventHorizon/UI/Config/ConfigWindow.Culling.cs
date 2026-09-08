using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using EventHorizon.Culling;
using EventHorizon.Localization;

namespace EventHorizon.UI.Config;

internal partial class ConfigWindow
{
    #region Main Content
    private void DrawContent()
    {
        DrawCard(
            Loc.Text("Config.StatusSummary.Title"),
            () =>
            {
                DrawPlayerHidingMasterSwitch();
                ImGui.Spacing();
                DrawStatusSummaryContent();

                ImGui.Spacing();
                DrawCardSeparator();
                ImGui.Spacing();
                ImGui.TextDisabled(Loc.Text("Config.Section.HideTriggers"));
                DrawDutyRule();
                DrawLowPlayerCountRule();

                ImGui.Spacing();
                DrawCardSeparator();
                ImGui.Spacing();
                DrawVisiblePlayerLimitRule();
            }
        );

        var resetKeepRuleOrderLabel = Loc.Text("Config.KeepRuleOrder.Reset");
        var resetKeepRuleOrderWidth =
            ImGui.CalcTextSize(resetKeepRuleOrderLabel).X + (ImGui.GetStyle().FramePadding.X * 2f) + (ImGui.GetStyle().CellPadding.X * 2f);
        DrawCard(
            Loc.Text("Config.Section.KeepRules"),
            () =>
            {
                DrawKeepRules();

                ImGui.Spacing();
                DrawCardSeparator();
                ImGui.Spacing();
                ImGui.TextDisabled(Loc.Text("Config.Section.AttachedObjects"));
                DrawOtherPlayerCompanionRule();
                DrawOtherPlayerOrnamentRule();
                DrawOtherPlayerBattlePetRule();
                DrawUnattachedEventNpcRule();

                ImGui.Spacing();
                DrawCardSeparator();
                ImGui.Spacing();
                ImGui.TextDisabled(Loc.Text("Config.Section.PlayerDisplay"));
                DrawTemporaryShowAllPlayersKey();
            },
            () => DrawResetKeepRuleOrderButton(resetKeepRuleOrderLabel),
            resetKeepRuleOrderWidth
        );
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

    private void DrawStatusSummaryContent()
    {
        var cullingStatus = plugin.CullingStatus;
        var currentOtherPlayerCount = cullingStatus.OtherPlayerCount;
        var hiddenPlayerCount = plugin.HiddenPlayerCount;
        var keptOtherPlayerCount = Math.Max(0, currentOtherPlayerCount - hiddenPlayerCount);
        var suspensionReason = GetCullingSuspensionReason(cullingStatus);

        if (!configuration.HideAllOtherPlayers)
        {
            DrawSummaryRow(Loc.Text("Config.StatusSummary.State"), Loc.Text("Status.Disabled"));
        }
        else if (!string.IsNullOrEmpty(suspensionReason))
        {
            DrawSummaryRow(Loc.Text("Config.StatusSummary.State"), string.Format(Loc.Text("Status.Paused"), suspensionReason));
        }
        else
        {
            DrawSummaryRow(Loc.Text("Config.StatusSummary.State"), Loc.Text("Status.Running"));
        }

        DrawSummaryRow(Loc.Text("Config.StatusSummary.Fps"), GetFrameRateSummary());
        DrawSummaryRow(
            Loc.Text("Config.StatusSummary.VisibleHidden"),
            string.Format(Loc.Text("Config.StatusSummary.VisibleHidden.Value"), keptOtherPlayerCount, hiddenPlayerCount)
        );
    }

    private void DrawPlayerHidingMasterSwitch()
    {
        var statusText = configuration.HideAllOtherPlayers ? Loc.Text("Status.Enabled") : Loc.Text("Status.Disabled");
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

    private string GetCullingSuspensionReason(CullingStatus status)
    {
        return status.Mode switch
        {
            CullingRuntimeMode.SuspendedTemporaryReveal => Loc.Text("PauseReason.TemporaryReveal"),
            CullingRuntimeMode.SuspendedDuty => Loc.Text("PauseReason.InDuty"),
            CullingRuntimeMode.SuspendedLowPlayerCount => string.Format(
                Loc.Text("Config.StatusSummary.PauseReason.LowPlayerCount"),
                status.OtherPlayerCount,
                configuration.DisableCullingPlayerCountThreshold
            ),
            CullingRuntimeMode.PlayerUnavailable => Loc.Text("PauseReason.PlayerUnavailable"),
            CullingRuntimeMode.NativeHookFailed => Loc.Text("PauseReason.NativeHookFailed"),
            _ => string.Empty,
        };
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
        var limitVisiblePlayerCount = configuration.LimitVisiblePlayerCount;
        if (ImGui.Checkbox($"{label}###LimitVisiblePlayerCount", ref limitVisiblePlayerCount))
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
        if (ImGui.SliderInt("###VisiblePlayerCountLimit", ref limit, 0, 100))
        {
            configuration.VisiblePlayerCountLimit = Math.Clamp(limit, 0, 100);
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

    private void DrawOtherPlayerBattlePetRule()
    {
        var hideOtherPlayerBattlePets = configuration.HideOtherPlayerBattlePets;
        if (DrawAutoFitCheckbox("HideOtherPlayerBattlePets", Loc.Text("Config.HideOtherPlayerBattlePets"), ref hideOtherPlayerBattlePets))
        {
            configuration.HideOtherPlayerBattlePets = hideOtherPlayerBattlePets;
            SaveAndRefresh();
        }
    }

    private void DrawUnattachedEventNpcRule()
    {
        var hideUnattachedEventNpcs = configuration.HideUnattachedEventNpcs;
        if (DrawAutoFitCheckbox("HideUnattachedEventNpcs", Loc.Text("Config.HideUnattachedEventNpcs"), ref hideUnattachedEventNpcs))
        {
            configuration.HideUnattachedEventNpcs = hideUnattachedEventNpcs;
            SaveAndRefresh();
        }
    }

    #endregion
}
