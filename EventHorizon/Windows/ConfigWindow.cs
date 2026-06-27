using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
    private readonly Vector4 warningTextColor = new(1f, 0.72f, 0.24f, 1f);

    private Tab? pendingSelectedTab;
    private CompetitiveKeepRule? draggedCompetitiveKeepRule;
    private bool competitiveKeepRuleOrderChanged;
    private bool showRaceSexPriorityEditor;

    private readonly record struct ImGuiItemState(bool Hovered, bool Active = false);

    #region Lifecycle

    public ConfigWindow(Plugin plugin, IDataManager dataManager)
        : base($"{Loc.Text("Config.Title")}###EventHorizonConfig")
    {
        Size = new Vector2(640, 1000);
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

        pendingSelectedTab = null;
    }

    private void DrawCullingTab()
    {
        var hideAllOtherPlayers = configuration.HideAllOtherPlayers;
        if (ImGui.Checkbox(Loc.Text("Config.HideAllOtherPlayers"), ref hideAllOtherPlayers))
        {
            configuration.HideAllOtherPlayers = hideAllOtherPlayers;
            SaveAndRefresh();
        }

        DrawStatusOverview();

        if (!configuration.HideAllOtherPlayers)
        {
            return;
        }

        if (DrawCollapsibleSectionHeader(Loc.Text("Config.Section.HideTriggers")))
        {
            DrawDutyRule();
            DrawLowPlayerCountRule();
            ImGui.TreePop();
        }

        DrawSectionHeader(Loc.Text("Config.Section.VisiblePlayerBudget"), Loc.Text("Config.LimitVisiblePlayerCount.Help"));
        DrawVisiblePlayerLimitRule();

        DrawKeepRules();

        if (DrawCollapsibleSectionHeader(Loc.Text("Config.Section.AttachedObjects"), Loc.Text("Config.AttachedObjects.Help")))
        {
            DrawOtherPlayerCompanionRule();
            DrawOtherPlayerOrnamentRule();
            ImGui.TreePop();
        }
    }

    private void DrawBehaviorTab()
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
        var currentOtherPlayerCount = ObjectTableStats.CurrentOtherPlayerCount();
        var hiddenPlayerCount = plugin.HiddenPlayerCount;
        var keptOtherPlayerCount = Math.Max(0, currentOtherPlayerCount - hiddenPlayerCount);
        var suspensionReason = GetCullingSuspensionReason(currentOtherPlayerCount);

        ImGui.Spacing();
        if (!string.IsNullOrEmpty(suspensionReason))
        {
            ImGui.TextColored(warningTextColor, string.Format(Loc.Text("Config.StatusPaused"), suspensionReason));
            return;
        }

        ImGui.TextDisabled(string.Format(Loc.Text("Config.StatusRunning"), keptOtherPlayerCount, hiddenPlayerCount));
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

        if (configuration.DisableCullingBelowPlayerCount)
        {
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
    }

    private void DrawVisiblePlayerLimitRule()
    {
        var limitVisiblePlayerCount = configuration.LimitVisiblePlayerCount;
        if (ImGui.Checkbox(Loc.Text("Config.LimitVisiblePlayerCount"), ref limitVisiblePlayerCount))
        {
            configuration.LimitVisiblePlayerCount = limitVisiblePlayerCount;
            SaveAndRefresh();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        var limit = configuration.VisiblePlayerCountLimit;
        if (!configuration.LimitVisiblePlayerCount)
        {
            ImGui.BeginDisabled();
        }

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

        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Text("Config.VisiblePlayerCountLimitSuffix"));
        ImGui.SameLine();
        ImGui.TextDisabled(Loc.Text("Config.ProtectedPlayersIgnoreLimit"));
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
        DrawSectionHeader(Loc.Text("Config.Section.KeepRules"), Loc.Text("Config.KeepRules.Help"));

        if (!ImGui.BeginTable("###CompetitiveKeepOrderTable", 2, ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("###CompetitiveKeepOrderHandle", ImGuiTableColumnFlags.WidthFixed, 28f);
        ImGui.TableSetupColumn("###CompetitiveKeepOrderRule", ImGuiTableColumnFlags.WidthStretch);

        DrawKeepRuleGroupHeader(Loc.Text("Config.KeepRules.Protected"));
        DrawProtectedKeepRuleRows();

        if (DrawKeepRuleGroupHeader(Loc.Text("Config.KeepRules.Priority"), showResetButton: true))
        {
            CompetitiveKeepOrder.Reset(configuration);
            SaveAndRefreshWithoutRuleReset();
        }

        foreach (var rule in CompetitiveKeepOrder.GetEffectiveOrder(configuration))
        {
            DrawCompetitiveKeepRuleOrderRow(rule);
        }

        ImGui.EndTable();

        if (showRaceSexPriorityEditor)
        {
            ImGui.Indent();
            DrawRaceFilterEditor();
            ImGui.Unindent();
        }

        if (draggedCompetitiveKeepRule.HasValue && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            draggedCompetitiveKeepRule = null;
            if (competitiveKeepRuleOrderChanged)
            {
                competitiveKeepRuleOrderChanged = false;
                SaveAndRefreshWithoutRuleReset();
            }
        }
    }

    private static bool DrawKeepRuleGroupHeader(string label, bool showResetButton = false)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);

        if (!showResetButton)
        {
            return false;
        }

        ImGui.SameLine();
        return ImGui.SmallButton(Loc.Text("Config.CompetitiveKeepOrder.Reset"));
    }

    private void DrawProtectedKeepRuleRows()
    {
        var keepTargetAndFocusPlayers = configuration.KeepTargetAndFocusPlayers;
        DrawProtectedKeepRuleRow(
            Loc.Text("Config.KeepTargetAndFocusPlayers"),
            ref keepTargetAndFocusPlayers,
            Loc.Text("Config.KeepTargetAndFocusPlayers.Help"),
            value => configuration.KeepTargetAndFocusPlayers = value
        );

        var keepPartyAndAllianceMembers = configuration.KeepPartyAndAllianceMembers;
        DrawProtectedKeepRuleRow(
            Loc.Text("Config.KeepPartyAndAllianceMembers"),
            ref keepPartyAndAllianceMembers,
            string.Empty,
            value => configuration.KeepPartyAndAllianceMembers = value
        );

        var keepFriends = configuration.KeepFriends;
        DrawProtectedKeepRuleRow(Loc.Text("Config.KeepFriends"), ref keepFriends, string.Empty, value => configuration.KeepFriends = value);
    }

    private void DrawProtectedKeepRuleRow(string label, ref bool value, string helpText, Action<bool> setValue)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.Empty);

        ImGui.TableNextColumn();
        if (ImGui.Checkbox(label, ref value))
        {
            setValue(value);
            SaveAndRefresh();
        }

        if (!string.IsNullOrEmpty(helpText))
        {
            DrawHelpMarker(helpText);
        }
    }

    private void DrawCompetitiveKeepRuleOrderRow(CompetitiveKeepRule rule)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var handleState = DrawCompetitiveKeepRuleHandle(rule);

        ImGui.TableNextColumn();
        var ruleItemState = DrawCompetitiveKeepRuleControl(rule);

        if (handleState.Active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            draggedCompetitiveKeepRule = rule;
        }

        if (
            draggedCompetitiveKeepRule.HasValue
            && draggedCompetitiveKeepRule.Value != rule
            && (handleState.Hovered || ruleItemState.Hovered)
        )
        {
            MoveCompetitiveKeepRuleTo(draggedCompetitiveKeepRule.Value, rule);
        }
    }

    private static ImGuiItemState DrawCompetitiveKeepRuleHandle(CompetitiveKeepRule rule)
    {
        var handleSize = new Vector2(20f, ImGui.GetTextLineHeightWithSpacing());
        ImGui.InvisibleButton($"###CompetitiveKeepRuleHandle{rule}", handleSize);

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
        var left = min.X + (max.X - min.X - width) * 0.5f;
        var centerY = (min.Y + max.Y) * 0.5f;

        for (var i = -1; i <= 1; i++)
        {
            var y = centerY + i * 4f;
            drawList.AddLine(new Vector2(left, y), new Vector2(left + width, y), color, 1.5f);
        }
    }

    private ImGuiItemState DrawCompetitiveKeepRuleControl(CompetitiveKeepRule rule)
    {
        var enabled = IsCompetitiveKeepRuleEnabled(rule);
        if (ImGui.Checkbox($"{GetCompetitiveKeepRuleLabel(rule)}###CompetitiveKeepRule{rule}", ref enabled))
        {
            SetCompetitiveKeepRuleEnabled(rule, enabled);
            SaveAndRefresh();
        }
        var itemState = new ImGuiItemState(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem));

        switch (rule)
        {
            case CompetitiveKeepRule.TargetingMe:
                DrawHelpMarker(Loc.Text("Config.KeepPlayersTargetingMe.Help"));
                break;
            case CompetitiveKeepRule.RecentChat:
                DrawHelpMarker(Loc.Text("Config.KeepRecentChatPlayers.Help"));
                break;
            case CompetitiveKeepRule.Nearby:
                DrawNearbyPlayerPriorityOptions();
                break;
            case CompetitiveKeepRule.Race:
                ImGui.SameLine();
                if (ImGui.SmallButton(Loc.Text("Config.RaceFilter.Edit")))
                {
                    showRaceSexPriorityEditor = !showRaceSexPriorityEditor;
                }
                break;
        }

        return itemState;
    }

    private void DrawNearbyPlayerPriorityOptions()
    {
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Text("Config.KeepNearbyPlayersRange"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140f);
        var range = configuration.KeepNearbyPlayersRange;
        if (ImGui.SliderFloat("###KeepNearbyPlayersRange", ref range, 1f, 50f, Loc.Text("Config.DistanceSliderFormat")))
        {
            configuration.KeepNearbyPlayersRange = Math.Clamp(range, 1f, 50f);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveAndRefresh();
        }
    }

    private bool IsCompetitiveKeepRuleEnabled(CompetitiveKeepRule rule) =>
        rule switch
        {
            CompetitiveKeepRule.TargetingMe => configuration.KeepPlayersTargetingMe,
            CompetitiveKeepRule.RecentChat => configuration.KeepRecentChatPlayers,
            CompetitiveKeepRule.Recruiting => configuration.KeepRecruitingPlayers,
            CompetitiveKeepRule.Nearby => configuration.KeepNearbyPlayers,
            CompetitiveKeepRule.Race => configuration.KeepSelectedRaces,
            _ => false,
        };

    private void SetCompetitiveKeepRuleEnabled(CompetitiveKeepRule rule, bool enabled)
    {
        switch (rule)
        {
            case CompetitiveKeepRule.TargetingMe:
                configuration.KeepPlayersTargetingMe = enabled;
                break;
            case CompetitiveKeepRule.RecentChat:
                configuration.KeepRecentChatPlayers = enabled;
                break;
            case CompetitiveKeepRule.Recruiting:
                configuration.KeepRecruitingPlayers = enabled;
                break;
            case CompetitiveKeepRule.Nearby:
                configuration.KeepNearbyPlayers = enabled;
                break;
            case CompetitiveKeepRule.Race:
                configuration.KeepSelectedRaces = enabled;
                break;
        }
    }

    private void MoveCompetitiveKeepRuleTo(CompetitiveKeepRule dragged, CompetitiveKeepRule target)
    {
        var order = new List<CompetitiveKeepRule>(CompetitiveKeepOrder.GetEffectiveOrder(configuration));
        var from = order.IndexOf(dragged);
        var to = order.IndexOf(target);
        if (from < 0 || to < 0 || from == to)
        {
            return;
        }

        order.RemoveAt(from);
        order.Insert(Math.Min(to, order.Count), dragged);
        configuration.CompetitiveKeepRuleOrder = order;
        competitiveKeepRuleOrderChanged = true;
    }

    private static string GetCompetitiveKeepRuleLabel(CompetitiveKeepRule rule) =>
        rule switch
        {
            CompetitiveKeepRule.TargetingMe => Loc.Text("Config.KeepPlayersTargetingMe"),
            CompetitiveKeepRule.RecentChat => Loc.Text("Config.KeepRecentChatPlayers"),
            CompetitiveKeepRule.Recruiting => Loc.Text("Config.KeepRecruitingPlayers"),
            CompetitiveKeepRule.Nearby => Loc.Text("Config.KeepNearbyPlayers"),
            CompetitiveKeepRule.Race => Loc.Text("Config.KeepRaceFilter"),
            _ => rule.ToString(),
        };

    private static void DrawHelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("?");

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static void DrawSectionHeader(string label, string helpText = "")
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted(label);
        if (!string.IsNullOrEmpty(helpText))
        {
            DrawHelpMarker(helpText);
        }
        ImGui.Spacing();
    }

    private static bool DrawCollapsibleSectionHeader(string label, string helpText = "")
    {
        ImGui.Spacing();
        var open = ImGui.TreeNode(label);
        if (!string.IsNullOrEmpty(helpText))
        {
            DrawHelpMarker(helpText);
        }

        return open;
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
                3,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV
            )
        )
        {
            return;
        }

        ImGui.TableSetupColumn(Loc.Text("Config.RaceFilter.Race"));
        ImGui.TableSetupColumn(Loc.Text("Config.RaceFilter.Male"));
        ImGui.TableSetupColumn(Loc.Text("Config.RaceFilter.Female"));
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Loc.Text("Config.RaceFilter.Race"));
        ImGui.TableNextColumn();
        DrawSexColumnHeader(RaceSexFilter.MaleSex, Loc.Text("Config.RaceFilter.Male"));
        ImGui.TableNextColumn();
        DrawSexColumnHeader(RaceSexFilter.FemaleSex, Loc.Text("Config.RaceFilter.Female"));

        for (var race = RaceSexFilter.MinRace; race <= RaceSexFilter.MaxRace; race++)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawRaceRowHeader(race);

            DrawRaceSexFilterCell(race, RaceSexFilter.MaleSex);
            DrawRaceSexFilterCell(race, RaceSexFilter.FemaleSex);
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
