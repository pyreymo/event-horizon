using Dalamud.Bindings.ImGui;
using EventHorizon.Culling;
using EventHorizon.Localization;
using Lumina.Excel.Sheets;

namespace EventHorizon.UI.Config;

internal partial class ConfigWindow
{
    #region Race/Sex Filter

    private void DrawRaceFilterEditorInline()
    {
        ImGui.Spacing();
        ImGui.Indent(66f);

        DrawRaceFilterEditor();

        ImGui.Unindent(66f);
        ImGui.Spacing();
    }

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
