using System;
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
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly IDataManager dataManager;
    private readonly Vector4 warningTextColor = new(1f, 0.72f, 0.24f, 1f);

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

    #endregion

    #region Draw

    public override void PreDraw()
    {
        WindowName = $"{Loc.Text("Config.Title")}###EventHorizonConfig";
    }

    public override void Draw()
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Indent();
        DrawDutyRule();
        DrawLowPlayerCountRule();
        DrawVisiblePlayerLimitRule();
        ImGui.Spacing();
        DrawOtherPlayerCompanionRule();
        DrawOtherPlayerOrnamentRule();
        ImGui.Spacing();
        DrawFriendKeepRule();
        DrawPartyKeepRule();
        DrawRecruitingKeepRule();
        DrawRecentChatKeepRule();
        DrawTargetKeepRule();
        DrawTargetingMeKeepRule();
        DrawNearbyPlayerKeepRule();
        ImGui.Spacing();
        DrawRaceFilter();
        ImGui.Unindent();
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
        var currentPlayerCount = ObjectTableStats.CurrentPlayerCount();

        ImGui.Spacing();
        if (ImGui.BeginTable("###EventHorizonStatusOverview", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            ImGui.TextDisabled(
                string.Format(Loc.Text("Config.CurrentPlayerCount"), currentPlayerCount)
            );

            ImGui.TableNextColumn();
            ImGui.TextDisabled(
                string.Format(Loc.Text("Config.HiddenPlayerCount"), plugin.HiddenPlayerCount)
            );

            ImGui.EndTable();
        }

        if (IsLowPlayerCountCullingSuspended(currentPlayerCount))
        {
            ImGui.TextColored(warningTextColor, Loc.Text("Config.LowPlayerCountCullingSuspended"));
        }

        if (plugin.IsDutyCullingSuspended)
        {
            ImGui.TextColored(warningTextColor, Loc.Text("Config.DutyCullingSuspended"));
        }
    }

    private bool IsLowPlayerCountCullingSuspended(int currentPlayerCount)
    {
        return configuration.HideAllOtherPlayers
            && configuration.DisableCullingBelowPlayerCount
            && currentPlayerCount < configuration.DisableCullingPlayerCountThreshold;
    }

    private void DrawLowPlayerCountRule()
    {
        var disableCullingBelowPlayerCount = configuration.DisableCullingBelowPlayerCount;
        if (
            ImGui.Checkbox(
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

        ImGui.Indent();

        ImGui.SetNextItemWidth(120f);
        var threshold = configuration.DisableCullingPlayerCountThreshold;
        if (
            ImGui.SliderInt(
                Loc.Text("Config.DisableCullingPlayerCountThreshold"),
                ref threshold,
                1,
                200
            )
        )
        {
            configuration.DisableCullingPlayerCountThreshold = Math.Clamp(threshold, 1, 200);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveAndRefresh();
        }

        ImGui.Unindent();
    }

    private void DrawVisiblePlayerLimitRule()
    {
        var limitVisiblePlayerCount = configuration.LimitVisiblePlayerCount;
        if (ImGui.Checkbox(Loc.Text("Config.LimitVisiblePlayerCount"), ref limitVisiblePlayerCount))
        {
            configuration.LimitVisiblePlayerCount = limitVisiblePlayerCount;
            SaveAndRefresh();
        }
        DrawHelpMarker(Loc.Text("Config.LimitVisiblePlayerCount.Help"));

        if (!configuration.LimitVisiblePlayerCount)
        {
            return;
        }

        ImGui.Indent();

        ImGui.SetNextItemWidth(120f);
        var limit = configuration.VisiblePlayerCountLimit;
        if (ImGui.SliderInt(Loc.Text("Config.VisiblePlayerCountLimit"), ref limit, 1, 200))
        {
            configuration.VisiblePlayerCountLimit = Math.Clamp(limit, 1, 200);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveAndRefresh();
        }

        ImGui.Unindent();
    }

    private void DrawOtherPlayerCompanionRule()
    {
        var hideOtherPlayerCompanions = configuration.HideOtherPlayerCompanions;
        if (
            ImGui.Checkbox(
                Loc.Text("Config.HideOtherPlayerCompanions"),
                ref hideOtherPlayerCompanions
            )
        )
        {
            configuration.HideOtherPlayerCompanions = hideOtherPlayerCompanions;
            SaveAndRefresh();
        }
    }

    private void DrawOtherPlayerOrnamentRule()
    {
        var hideOtherPlayerOrnaments = configuration.HideOtherPlayerOrnaments;
        if (
            ImGui.Checkbox(
                Loc.Text("Config.HideOtherPlayerOrnaments"),
                ref hideOtherPlayerOrnaments
            )
        )
        {
            configuration.HideOtherPlayerOrnaments = hideOtherPlayerOrnaments;
            SaveAndRefresh();
        }
    }

    private void DrawFriendKeepRule()
    {
        var keepFriends = configuration.KeepFriends;
        if (ImGui.Checkbox(Loc.Text("Config.KeepFriends"), ref keepFriends))
        {
            configuration.KeepFriends = keepFriends;
            SaveAndRefresh();
        }
    }

    private void DrawPartyKeepRule()
    {
        var keepPartyAndAllianceMembers = configuration.KeepPartyAndAllianceMembers;
        if (
            ImGui.Checkbox(
                Loc.Text("Config.KeepPartyAndAllianceMembers"),
                ref keepPartyAndAllianceMembers
            )
        )
        {
            configuration.KeepPartyAndAllianceMembers = keepPartyAndAllianceMembers;
            SaveAndRefresh();
        }
    }

    private void DrawRecruitingKeepRule()
    {
        var keepRecruitingPlayers = configuration.KeepRecruitingPlayers;
        if (ImGui.Checkbox(Loc.Text("Config.KeepRecruitingPlayers"), ref keepRecruitingPlayers))
        {
            configuration.KeepRecruitingPlayers = keepRecruitingPlayers;
            SaveAndRefresh();
        }
    }

    private void DrawRecentChatKeepRule()
    {
        var keepRecentChatPlayers = configuration.KeepRecentChatPlayers;
        if (ImGui.Checkbox(Loc.Text("Config.KeepRecentChatPlayers"), ref keepRecentChatPlayers))
        {
            configuration.KeepRecentChatPlayers = keepRecentChatPlayers;
            SaveAndRefresh();
        }
        DrawHelpMarker(Loc.Text("Config.KeepRecentChatPlayers.Help"));
    }

    private void DrawTargetKeepRule()
    {
        var keepTargetAndFocusPlayers = configuration.KeepTargetAndFocusPlayers;
        if (
            ImGui.Checkbox(
                Loc.Text("Config.KeepTargetAndFocusPlayers"),
                ref keepTargetAndFocusPlayers
            )
        )
        {
            configuration.KeepTargetAndFocusPlayers = keepTargetAndFocusPlayers;
            SaveAndRefresh();
        }
        DrawHelpMarker(Loc.Text("Config.KeepTargetAndFocusPlayers.Help"));
    }

    private void DrawTargetingMeKeepRule()
    {
        var keepPlayersTargetingMe = configuration.KeepPlayersTargetingMe;
        if (ImGui.Checkbox(Loc.Text("Config.KeepPlayersTargetingMe"), ref keepPlayersTargetingMe))
        {
            configuration.KeepPlayersTargetingMe = keepPlayersTargetingMe;
            SaveAndRefresh();
        }
        DrawHelpMarker(Loc.Text("Config.KeepPlayersTargetingMe.Help"));
    }

    private void DrawNearbyPlayerKeepRule()
    {
        var keepNearbyPlayers = configuration.KeepNearbyPlayers;
        if (ImGui.Checkbox(Loc.Text("Config.KeepNearbyPlayers"), ref keepNearbyPlayers))
        {
            configuration.KeepNearbyPlayers = keepNearbyPlayers;
            SaveAndRefresh();
        }

        if (!configuration.KeepNearbyPlayers)
        {
            return;
        }

        ImGui.Indent();

        ImGui.SetNextItemWidth(180f);
        var range = configuration.KeepNearbyPlayersRange;
        if (
            ImGui.SliderFloat(Loc.Text("Config.KeepNearbyPlayersRange"), ref range, 1f, 50f, "%.1f")
        )
        {
            configuration.KeepNearbyPlayersRange = Math.Clamp(range, 1f, 50f);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveAndRefresh();
        }

        ImGui.SameLine();

        var previewNearbyPlayerRange = configuration.PreviewNearbyPlayerRange;
        if (
            ImGui.Checkbox(
                Loc.Text("Config.PreviewNearbyPlayerRange"),
                ref previewNearbyPlayerRange
            )
        )
        {
            configuration.PreviewNearbyPlayerRange = previewNearbyPlayerRange;
            configuration.Save();
        }

        ImGui.Unindent();
    }

    private static void DrawHelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");

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

    #endregion

    #region Race/Sex Filter

    private void DrawRaceFilter()
    {
        var keepSelectedRaces = configuration.KeepSelectedRaces;
        if (ImGui.Checkbox(Loc.Text("Config.KeepRaceFilter"), ref keepSelectedRaces))
        {
            configuration.KeepSelectedRaces = keepSelectedRaces;
            SaveAndRefresh();
        }

        if (!configuration.KeepSelectedRaces)
        {
            return;
        }

        ImGui.Indent();

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
                ImGuiTableFlags.SizingFixedFit
                    | ImGuiTableFlags.RowBg
                    | ImGuiTableFlags.BordersInnerV
            )
        )
        {
            ImGui.Unindent();
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
        ImGui.Unindent();
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
            && configuration.KeptRaceSex.Contains(
                RaceSexFilter.Pack(race, RaceSexFilter.FemaleSex)
            );

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
