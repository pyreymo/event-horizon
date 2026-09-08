using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using EventHorizon.Culling;
using EventHorizon.Localization;
using EventHorizon.Settings;

namespace EventHorizon.UI.Config;

internal sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin)
        : base("Event Horizon###EventHorizonConfig")
    {
        this.plugin = plugin;
        configuration = plugin.Configuration;
        Size = new Vector2(620, 790);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 480), MaximumSize = new Vector2(900, 1400) };
    }

    public override void PreDraw() => CrowdUi.PushStyle();

    public override void PostDraw() => CrowdUi.PopStyle();

    public override void Draw()
    {
        var status = plugin.CullingStatus;
        ImGui.TextColored(CrowdUi.Accent, "EVENT HORIZON");
        ImGui.TextWrapped(Loc.Text("Crowd.Tagline"));
        ImGui.Spacing();
        Toggle("Crowd.Enabled", configuration.HideAllOtherPlayers, value => configuration.HideAllOtherPlayers = value);
        ImGui.SameLine();
        ImGui.TextDisabled(CrowdUi.State(status));

        CrowdUi.Section("Crowd.Limit");
        ImGui.SetWindowFontScale(2f);
        ImGui.TextUnformatted(
            configuration.LimitVisiblePlayerCount ? configuration.VisiblePlayerCountLimit.ToString() : Loc.Text("Crowd.Unlimited")
        );
        ImGui.SetWindowFontScale(1f);
        Toggle("Crowd.UnlimitedToggle", !configuration.LimitVisiblePlayerCount, value => configuration.LimitVisiblePlayerCount = !value);
        var limit = configuration.VisiblePlayerCountLimit;
        ImGui.SetNextItemWidth(-1);
        ImGui.BeginDisabled(!configuration.LimitVisiblePlayerCount);
        if (ImGui.SliderInt("###PlayerLimit", ref limit, 0, 100, "%d", ImGuiSliderFlags.AlwaysClamp))
        {
            configuration.VisiblePlayerCountLimit = limit;
            plugin.RefreshObjectCulling();
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Save(false);
        ImGui.EndDisabled();
        CrowdUi.Hint(Loc.Text("Crowd.LimitHelp"));
        if (status.Mode == CullingRuntimeMode.Active)
            ImGui.TextWrapped(
                string.Format(
                    Loc.Text("Crowd.Counts"),
                    status.OtherPlayerCount,
                    status.Admitted,
                    status.Rejected,
                    Math.Max(0, status.OtherPlayerCount - status.Admitted - status.Rejected)
                )
            );
        else
            ImGui.TextWrapped(string.Format(Loc.Text("Crowd.PausedCounts"), status.OtherPlayerCount));

        CrowdUi.Section("Crowd.Always");
        if (ImGui.BeginTable("###Relationships", 3, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            DrawRelationship(PlayerKeepRuleId.PartyAlliance);
            ImGui.TableNextColumn();
            DrawRelationship(PlayerKeepRuleId.Friends);
            ImGui.TableNextColumn();
            DrawRelationship(PlayerKeepRuleId.TargetFocus);
            ImGui.EndTable();
        }
        var additional = PlayerKeepRuleOrder
            .GetEffectiveOrder(configuration)
            .Where(rule =>
                rule is not (PlayerKeepRuleId.PartyAlliance or PlayerKeepRuleId.Friends or PlayerKeepRuleId.TargetFocus)
                && configuration.GetTreatment(rule) == RuleTreatment.Always
            )
            .Select(CrowdUi.RuleName)
            .ToArray();
        if (additional.Length > 0)
            ImGui.TextWrapped(string.Format(Loc.Text("Crowd.AdditionalAlways"), string.Join(" · ", additional)));
        CrowdUi.Hint(Loc.Text("Crowd.AlwaysHelp"));

        CrowdUi.Section("Crowd.Prefer");
        var preferred = PlayerKeepRuleOrder
            .GetEffectiveOrder(configuration)
            .Where(rule => configuration.GetTreatment(rule) == RuleTreatment.Prefer)
            .Select(CrowdUi.RuleName);
        ImGui.TextWrapped(string.Join("  →  ", preferred.Append(Loc.Text("Crowd.EveryoneElse"))));
        if (ImGui.CollapsingHeader(Loc.Text("Crowd.EditPreferences")))
            DrawPreferences();

        ImGui.Spacing();
        if (ImGui.Button(Loc.Text("Inspector.Open"), new Vector2(-1, 42)))
            plugin.OpenPlayerInspector();
        CrowdUi.Hint(Loc.Text("Crowd.GameLimits"));

        CrowdUi.Section("Crowd.Automatic");
        Toggle("Crowd.Duty", configuration.DisableInDuty, value => configuration.DisableInDuty = value);
        Toggle(
            "Crowd.RevealShortcut",
            configuration.EnableTemporaryShowAllPlayersShortcut,
            value => configuration.EnableTemporaryShowAllPlayersShortcut = value
        );
        if (configuration.EnableTemporaryShowAllPlayersShortcut)
        {
            CrowdUi.Hint(
                configuration.TemporarilyShowAllPlayersKeys.Count == 0
                    ? Loc.Text("Crowd.ShortcutUnset")
                    : string.Format(
                        Loc.Text("Crowd.ShortcutKeys"),
                        string.Join(" + ", configuration.TemporarilyShowAllPlayersKeys.Select(KeyName))
                    )
            );
            if (ImGui.SmallButton(Loc.Text("Crowd.ResetShortcut")))
            {
                configuration.TemporarilyShowAllPlayersKeys = [0x11, 0x12];
                Save();
            }
        }
        if (ImGui.CollapsingHeader(Loc.Text("Crowd.More")))
        {
            Toggle(
                "Crowd.Quiet",
                configuration.DisableCullingBelowPlayerCount,
                value => configuration.DisableCullingBelowPlayerCount = value
            );
            if (configuration.DisableCullingBelowPlayerCount)
            {
                var threshold = configuration.DisableCullingPlayerCountThreshold;
                if (ImGui.SliderInt(Loc.Text("Crowd.QuietThreshold"), ref threshold, 1, 100, "%d", ImGuiSliderFlags.AlwaysClamp))
                {
                    configuration.DisableCullingPlayerCountThreshold = threshold;
                    plugin.RefreshObjectCulling();
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                    Save(false);
            }
            Toggle("Crowd.Companions", configuration.HideOtherPlayerCompanions, value => configuration.HideOtherPlayerCompanions = value);
            Toggle("Crowd.Ornaments", configuration.HideOtherPlayerOrnaments, value => configuration.HideOtherPlayerOrnaments = value);
            Toggle("Crowd.Pets", configuration.HideOtherPlayerBattlePets, value => configuration.HideOtherPlayerBattlePets = value);
            Toggle("Crowd.EventNpcs", configuration.HideUnattachedEventNpcs, value => configuration.HideUnattachedEventNpcs = value);
            Toggle("Crowd.Dtr", configuration.ShowDtrBar, value => configuration.ShowDtrBar = value);
        }
    }

    private void DrawRelationship(PlayerKeepRuleId rule)
    {
        var treatment = configuration.GetTreatment(rule);
        var always = treatment == RuleTreatment.Always;
        if (ImGui.Checkbox(CrowdUi.RuleName(rule), ref always))
        {
            configuration.SetTreatment(rule, always ? RuleTreatment.Always : RuleTreatment.Ordinary);
            Save();
        }
        if (treatment == RuleTreatment.Prefer)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(Loc.Text("Treatment.Prefer"));
        }
    }

    private void DrawPreferences()
    {
        CrowdUi.Hint(Loc.Text("Crowd.PreferenceHelp"));
        CrowdUi.Hint(Loc.Text("Crowd.InteractionHelp"));
        var order = PlayerKeepRuleOrder.GetEffectiveOrder(configuration).ToList();
        if (!ImGui.BeginTable("###Preferences", 3, ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Treatment", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, 82);
        for (var index = 0; index < order.Count; index++)
        {
            var rule = order[index];
            ImGui.PushID((int)rule);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(CrowdUi.RuleName(rule));
            ImGui.TableNextColumn();
            var choice = (int)configuration.GetTreatment(rule);
            var labels = new[] { Loc.Text("Treatment.Ordinary"), Loc.Text("Treatment.Prefer"), Loc.Text("Treatment.Always") };
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("###Treatment", ref choice, labels, labels.Length))
            {
                configuration.SetTreatment(rule, (RuleTreatment)choice);
                Save();
            }
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(index == 0);
            var up = ImGui.SmallButton("↑");
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(index == order.Count - 1);
            var down = ImGui.SmallButton("↓");
            ImGui.EndDisabled();
            if (up || down)
            {
                var destination = index + (up ? -1 : 1);
                var updatedOrder = order.ToList();
                (updatedOrder[index], updatedOrder[destination]) = (updatedOrder[destination], updatedOrder[index]);
                configuration.KeepRuleOrder = updatedOrder;
                Save(false);
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (configuration.GetTreatment(PlayerKeepRuleId.Nearby) != RuleTreatment.Ordinary)
        {
            var range = configuration.KeepNearbyPlayersRange;
            if (ImGui.SliderFloat(Loc.Text("Crowd.NearbyRange"), ref range, 1, 50, "%.0f m", ImGuiSliderFlags.AlwaysClamp))
            {
                configuration.KeepNearbyPlayersRange = range;
                plugin.RefreshObjectCulling();
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
                Save(false);
        }
        if (configuration.GetTreatment(PlayerKeepRuleId.Race) != RuleTreatment.Ordinary)
            DrawRaces();
    }

    private void DrawRaces()
    {
        if (!ImGui.BeginTable("###Races", 3, ImGuiTableFlags.SizingStretchProp))
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
                if (ImGui.Checkbox($"###Race{key}", ref selected))
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

    private void Save(bool resetRules = true)
    {
        configuration.Save();
        plugin.RefreshObjectCulling(resetRules);
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
