using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using EventHorizon.Culling;
using EventHorizon.Localization;
using EventHorizon.Settings;
using static EventHorizon.UI.CrowdUi;

namespace EventHorizon.UI.Config;

internal sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin)
        : base("Event Horizon###EventHorizonConsole")
    {
        this.plugin = plugin;
        configuration = plugin.Configuration;
        // A new window ID avoids inheriting the old, tall settings window's saved geometry.
        Size = new Vector2(S(810), S(540));
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(S(780), S(520)),
            MaximumSize = new Vector2(S(1040), S(800)),
        };
    }

    public override void PreDraw() => PushStyle();

    public override void PostDraw() => PopStyle();

    public override void Draw()
    {
        var status = plugin.CullingStatus;
        DrawHeader(status);
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(S(12), 0));
        if (ImGui.BeginTable("##Console", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Controls", ImGuiTableColumnFlags.WidthFixed, S(232));
            ImGui.TableSetupColumn("Rules", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("##Controls", Vector2.Zero))
                DrawControls(status);
            ImGui.EndChild();
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("##Rules", Vector2.Zero))
                DrawRules();
            ImGui.EndChild();
            ImGui.EndTable();
        }
        ImGui.PopStyleVar();
    }

    private void DrawHeader(CullingStatus status)
    {
        if (!ImGui.BeginTable("##Header", 3, ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Options", ImGuiTableColumnFlags.WidthFixed, S(76));
        ImGui.TableSetupColumn("Power", ImGuiTableColumnFlags.WidthFixed, S(90));
        ImGui.TableNextColumn();
        StateIndicator(status);
        ImGui.TableNextColumn();
        if (ImGui.Button(Loc.Text("Panel.Options"), new Vector2(-1, 0)))
            ImGui.OpenPopup("##Options");
        DrawOptions();
        ImGui.TableNextColumn();
        if (
            Chip(
                "power",
                Loc.Text(configuration.HideAllOtherPlayers ? "Panel.On" : "Panel.Off"),
                configuration.HideAllOtherPlayers,
                new Vector2(-1, 0),
                Accent
            )
        )
        {
            configuration.HideAllOtherPlayers = !configuration.HideAllOtherPlayers;
            Save();
        }
        ImGui.EndTable();
    }

    private void DrawControls(CullingStatus status)
    {
        Caption(Loc.Text("Panel.Limit"));
        Tooltip(Loc.Text("Panel.LimitHelp"));
        var limit = configuration.VisiblePlayerCountLimit;
        ImGui.BeginDisabled(!configuration.LimitVisiblePlayerCount);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.SetWindowFontScale(2.4f);
        ImGui.SetNextItemWidth(S(112));
        if (ImGui.DragInt("##Quota", ref limit, 0.2f, 0, 100, "%d", ImGuiSliderFlags.AlwaysClamp))
        {
            configuration.VisiblePlayerCountLimit = limit;
            plugin.RefreshObjectCulling();
        }
        var finishEdit = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SetWindowFontScale(1);
        ImGui.PopStyleColor();
        Tooltip(Loc.Text("Panel.EditNumber"));
        ImGui.EndDisabled();
        if (finishEdit)
            Save();
        ImGui.SameLine();
        if (Chip("unlimited", "∞", !configuration.LimitVisiblePlayerCount, new Vector2(S(44), S(44)), Accent))
        {
            configuration.LimitVisiblePlayerCount = !configuration.LimitVisiblePlayerCount;
            Save();
        }
        Tooltip(Loc.Text("Panel.Unlimited"));
        ImGui.BeginDisabled(!configuration.LimitVisiblePlayerCount);
        ImGui.SetNextItemWidth(-1);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, S(1)));
        if (ImGui.SliderInt("##QuotaSlider", ref limit, 0, 100, "", ImGuiSliderFlags.AlwaysClamp))
        {
            configuration.VisiblePlayerCountLimit = limit;
            plugin.RefreshObjectCulling();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save();
        ImGui.PopStyleVar();
        ImGui.EndDisabled();
        ImGui.Spacing();
        Caption(string.Format(Loc.Text("Panel.Loaded"), status.OtherPlayerCount));
        Population(status);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(S(2), 0));
        if (ImGui.BeginTable("##Counts", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            Metric("Panel.Admitted", status.Mode == CullingRuntimeMode.Active ? status.Admitted : null, Accent);
            ImGui.TableNextColumn();
            Metric("Panel.Rejected", status.Mode == CullingRuntimeMode.Active ? status.Rejected : null, Muted);
            ImGui.EndTable();
        }
        ImGui.PopStyleVar();
        var pending = status.OtherPlayerCount - status.Admitted - status.Rejected;
        if (status.Mode == CullingRuntimeMode.Active && pending > 0)
            ImGui.TextColored(Gold, string.Format(Loc.Text("Panel.Pending"), pending));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        Toggle("Panel.Duty", configuration.DisableInDuty, value => configuration.DisableInDuty = value);
        Tooltip(Loc.Text("Crowd.Duty"));
        Toggle("Panel.Quiet", configuration.DisableCullingBelowPlayerCount, value => configuration.DisableCullingBelowPlayerCount = value);
        ImGui.SameLine();
        if (ImGui.SmallButton($"< {configuration.DisableCullingPlayerCountThreshold}"))
            ImGui.OpenPopup("##QuietCount");
        if (ImGui.BeginPopup("##QuietCount"))
        {
            Caption(Loc.Text("Crowd.QuietThreshold"));
            var threshold = configuration.DisableCullingPlayerCountThreshold;
            ImGui.SetNextItemWidth(S(200));
            if (ImGui.SliderInt("##Threshold", ref threshold, 1, 100, "%d", ImGuiSliderFlags.AlwaysClamp))
            {
                configuration.DisableCullingPlayerCountThreshold = threshold;
                plugin.RefreshObjectCulling();
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
                Save();
            ImGui.EndPopup();
        }
        ImGui.Spacing();
        if (ImGui.Button(Loc.Text("Panel.Players"), new Vector2(-1, S(32))))
            plugin.OpenPlayerInspector();
    }

    private static void Metric(string label, int? count, Vector4 color)
    {
        ImGui.SetWindowFontScale(1.5f);
        ImGui.TextColored(color, count?.ToString() ?? "—");
        ImGui.SetWindowFontScale(1);
        ImGui.SameLine(0, S(5));
        ImGui.AlignTextToFramePadding();
        Caption(Loc.Text(label));
        Tooltip(Loc.Text("Crowd.GameLimits"));
    }

    private void DrawRules()
    {
        Caption(Loc.Text("Panel.Rules"));
        var order = PlayerKeepRuleOrder.GetEffectiveOrder(configuration).ToList();
        var rows = order
            .Select(rule => (Rule: rule, Treatment: configuration.GetTreatment(rule)))
            .OrderBy(row =>
                row.Treatment == RuleTreatment.Always ? 0
                : row.Treatment == RuleTreatment.Prefer ? 1
                : 2
            )
            .ToArray();
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(S(3), S(4)));
        if (ImGui.BeginTable("##RuleMatrix", 6, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, S(58));
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Parameter", ImGuiTableColumnFlags.WidthFixed, S(42));
            ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, S(44));
            ImGui.TableSetupColumn("Prefer", ImGuiTableColumnFlags.WidthFixed, S(44));
            ImGui.TableSetupColumn("Normal", ImGuiTableColumnFlags.WidthFixed, S(44));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Caption(Loc.Text("Panel.Order"));
            Tooltip(Loc.Text("Panel.OrderHelp"));
            ImGui.TableNextColumn();
            Caption(Loc.Text("Panel.Relationship"));
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ColumnLabel("Panel.Keep", Gold, "Panel.KeepHelp");
            ImGui.TableNextColumn();
            ColumnLabel("Panel.Prefer", Accent, "Panel.PreferHelp");
            ImGui.TableNextColumn();
            ColumnLabel("Panel.Normal", Muted, "Panel.NormalHelp");
            foreach (var row in rows)
            {
                ImGui.PushID((int)row.Rule);
                ImGui.TableNextRow(ImGuiTableRowFlags.None, S(34));
                ImGui.TableNextColumn();
                var group = rows.Where(other => other.Treatment == row.Treatment).Select(other => other.Rule).ToArray();
                var position = Array.IndexOf(group, row.Rule);
                DrawOrder(row.Rule, row.Treatment, group, position);
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(RuleName(row.Rule));
                Tooltip(Loc.Text($"RuleHelp.{row.Rule}"));
                ImGui.TableNextColumn();
                DrawRuleParameter(row.Rule);
                foreach (var treatment in new[] { RuleTreatment.Always, RuleTreatment.Prefer, RuleTreatment.Ordinary })
                {
                    ImGui.TableNextColumn();
                    if (RuleOption(treatment, row.Treatment == treatment, $"{RuleName(row.Rule)} · {Loc.Text($"Treatment.{treatment}")}"))
                    {
                        configuration.SetTreatment(row.Rule, treatment);
                        Save();
                    }
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.PopStyleVar();
        ImGui.Spacing();
        Caption(Loc.Text("Panel.RuleFooter"));
        Tooltip(Loc.Text("Panel.OrderHelp"));
        if (configuration.EnableTemporaryShowAllPlayersShortcut && configuration.TemporarilyShowAllPlayersKeys.Count > 0)
        {
            ImGui.Spacing();
            Caption(
                $"{string.Join(" + ", configuration.TemporarilyShowAllPlayersKeys.Select(KeyName))}  ·  {Loc.Text("Panel.ShortcutHint")}"
            );
        }
    }

    private static void ColumnLabel(string key, Vector4 color, string help)
    {
        var text = Loc.Text(key);
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (width - ImGui.CalcTextSize(text).X) / 2));
        ImGui.TextColored(color, text);
        Tooltip(Loc.Text(help));
    }

    private void DrawOrder(PlayerKeepRuleId rule, RuleTreatment treatment, PlayerKeepRuleId[] group, int position)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(treatment == RuleTreatment.Ordinary ? "—" : (position + 1).ToString("00"));
        if (treatment == RuleTreatment.Ordinary)
            return;
        ImGui.SameLine(0, S(3));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.BeginDisabled(position == 0);
        var up = ImGui.ArrowButton("##Up", ImGuiDir.Up);
        ImGui.EndDisabled();
        ImGui.SameLine(0, S(1));
        ImGui.BeginDisabled(position == group.Length - 1);
        var down = ImGui.ArrowButton("##Down", ImGuiDir.Down);
        ImGui.EndDisabled();
        ImGui.PopStyleVar();
        if (!up && !down)
            return;
        var order = PlayerKeepRuleOrder.GetEffectiveOrder(configuration).ToList();
        var source = order.IndexOf(rule);
        var destination = order.IndexOf(group[position + (up ? -1 : 1)]);
        (order[source], order[destination]) = (order[destination], order[source]);
        configuration.KeepRuleOrder = order;
        Save();
    }

    private void DrawRuleParameter(PlayerKeepRuleId rule)
    {
        if (rule == PlayerKeepRuleId.Nearby)
        {
            if (ImGui.SmallButton($"{configuration.KeepNearbyPlayersRange:0} m"))
                ImGui.OpenPopup("##Range");
            if (ImGui.BeginPopup("##Range"))
            {
                Caption(Loc.Text("Crowd.NearbyRange"));
                var range = configuration.KeepNearbyPlayersRange;
                ImGui.SetNextItemWidth(S(200));
                if (ImGui.SliderFloat("##Distance", ref range, 1, 50, "%.0f m", ImGuiSliderFlags.AlwaysClamp))
                {
                    configuration.KeepNearbyPlayersRange = range;
                    plugin.RefreshObjectCulling();
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                    Save();
                ImGui.EndPopup();
            }
        }
        else if (rule == PlayerKeepRuleId.Race)
        {
            if (ImGui.SmallButton($"{configuration.KeptRaceSex.Count}/16"))
                ImGui.OpenPopup("##Races");
            if (ImGui.BeginPopup("##Races"))
            {
                DrawRaces();
                ImGui.EndPopup();
            }
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            Caption(
                rule switch
                {
                    PlayerKeepRuleId.TargetFocus => "30s",
                    PlayerKeepRuleId.TargetingMe => "60s",
                    PlayerKeepRuleId.RecentChat => "5min",
                    _ => "",
                }
            );
            Tooltip(Loc.Text($"RuleHelp.{rule}"));
        }
    }

    private void DrawOptions()
    {
        if (!ImGui.BeginPopup("##Options"))
            return;
        Caption(Loc.Text("Panel.Attached"));
        Toggle("Crowd.Companions", configuration.HideOtherPlayerCompanions, value => configuration.HideOtherPlayerCompanions = value);
        Toggle("Crowd.Ornaments", configuration.HideOtherPlayerOrnaments, value => configuration.HideOtherPlayerOrnaments = value);
        Toggle("Crowd.Pets", configuration.HideOtherPlayerBattlePets, value => configuration.HideOtherPlayerBattlePets = value);
        Toggle("Crowd.EventNpcs", configuration.HideUnattachedEventNpcs, value => configuration.HideUnattachedEventNpcs = value);
        ImGui.Separator();
        Toggle(
            "Panel.Shortcut",
            configuration.EnableTemporaryShowAllPlayersShortcut,
            value => configuration.EnableTemporaryShowAllPlayersShortcut = value
        );
        Caption(
            configuration.TemporarilyShowAllPlayersKeys.Count == 0
                ? Loc.Text("Crowd.ShortcutUnset")
                : string.Join(" + ", configuration.TemporarilyShowAllPlayersKeys.Select(KeyName))
        );
        if (ImGui.SmallButton(Loc.Text("Crowd.ResetShortcut")))
        {
            configuration.TemporarilyShowAllPlayersKeys = [0x11, 0x12];
            Save();
        }
        ImGui.Separator();
        Toggle("Panel.Dtr", configuration.ShowDtrBar, value => configuration.ShowDtrBar = value);
        ImGui.EndPopup();
    }

    private void DrawRaces()
    {
        if (!ImGui.BeginTable("##RaceChoices", 3, ImGuiTableFlags.SizingFixedFit))
            return;
        ImGui.TableSetupColumn(Loc.Text("Crowd.Race"));
        ImGui.TableSetupColumn(Loc.Text("Crowd.Male"));
        ImGui.TableSetupColumn(Loc.Text("Crowd.Female"));
        ImGui.TableHeadersRow();
        for (byte race = 1; race <= 8; race++)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Loc.Text($"Race.{race}"));
            for (byte sex = 0; sex < 2; sex++)
            {
                ImGui.TableNextColumn();
                var key = RaceSexFilter.Pack(race, sex);
                var selected = configuration.KeptRaceSex.Contains(key);
                if (ImGui.Checkbox($"##Race{key}", ref selected))
                {
                    if (selected)
                        configuration.KeptRaceSex.Add(key);
                    else
                        configuration.KeptRaceSex.Remove(key);
                    Save();
                }
            }
        }
        ImGui.EndTable();
    }

    private void Toggle(string key, bool value, Action<bool> set)
    {
        if (!ImGui.Checkbox(Loc.Text(key), ref value))
            return;
        set(value);
        Save();
    }

    private void Save()
    {
        configuration.Save();
        plugin.RefreshObjectCulling();
        plugin.RefreshDtrBar();
    }

    private static string KeyName(int key) =>
        key switch
        {
            0x11 => "Ctrl",
            0x12 => "Alt",
            0x10 => "Shift",
            _ => ((Dalamud.Game.ClientState.Keys.VirtualKey)key).ToString(),
        };
}
