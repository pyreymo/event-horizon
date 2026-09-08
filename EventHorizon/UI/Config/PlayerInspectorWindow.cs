using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using EventHorizon.Culling;
using EventHorizon.Localization;
using EventHorizon.Settings;

namespace EventHorizon.UI.Config;

internal sealed class PlayerInspectorWindow : Window
{
    private readonly CullingController culling;
    private List<InspectedPlayer> players = [];
    private long nextRefresh;
    private string search = string.Empty;
    private bool rejectedOnly;

    public PlayerInspectorWindow(CullingController culling)
        : base("Players###EventHorizonInspector")
    {
        this.culling = culling;
        Size = new Vector2(780, 530);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(650, 340), MaximumSize = new Vector2(1400, 1200) };
    }

    public override void PreDraw()
    {
        WindowName = $"{Loc.Text("Inspector.Title")}###EventHorizonInspector";
        CrowdUi.PushStyle();
    }

    public override void PostDraw() => CrowdUi.PopStyle();

    public override void OnClose() => culling.StopReveal();

    public override void Draw()
    {
        var status = culling.GetStatus();
        ImGui.TextColored(CrowdUi.Accent, CrowdUi.State(status));
        CrowdUi.Hint(Loc.Text("Inspector.Help"));
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("###Search", Loc.Text("Inspector.Search"), ref search, 128);
        ImGui.SameLine();
        ImGui.Checkbox(Loc.Text("Inspector.RejectedOnly"), ref rejectedOnly);
        if (ImGui.SmallButton(Loc.Text("Inspector.StopReveal")))
            culling.StopReveal();

        var now = Environment.TickCount64;
        if (now >= nextRefresh)
        {
            players = culling.InspectPlayers();
            nextRefresh = now + 200;
        }
        if (players.Count == 0)
        {
            CrowdUi.Hint(Loc.Text("Inspector.Empty"));
            return;
        }
        if (
            !ImGui.BeginTable(
                "###Players",
                5,
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Max(100, ImGui.GetContentRegionAvail().Y))
            )
        )
            return;
        ImGui.TableSetupColumn(Loc.Text("Inspector.Player"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Loc.Text("Inspector.Decision"), ImGuiTableColumnFlags.WidthFixed, 108);
        ImGui.TableSetupColumn(Loc.Text("Inspector.Reason"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Loc.Text("Inspector.Distance"), ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("###Action", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        var shown = 0;
        foreach (var player in players)
        {
            // A paused controller has no current plugin decision, even if the cached row had one.
            var admission = status.Mode == CullingRuntimeMode.Active ? player.Admission : null;
            if (!player.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || (rejectedOnly && admission?.Allowed != false))
                continue;
            shown++;
            ImGui.PushID(player.Identity.GameObjectId.ToString("X"));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(player.Name) ? $"#{player.Identity.EntityId:X8}" : player.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(
                Loc.Text(
                    admission is { Allowed: false } ? "Inspector.Rejected"
                    : admission is { TemporaryReveal: true } ? "Inspector.Revealing"
                    : admission is { Decision.BudgetPolicy: PlayerKeepBudgetPolicy.Exempt } ? "Treatment.Always"
                    : admission.HasValue ? "Inspector.Admitted"
                    : "Inspector.Unmanaged"
                )
            );
            ImGui.TableNextColumn();
            ImGui.TextWrapped(Reason(admission, status));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{player.Distance:F0} m");
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(status.Mode != CullingRuntimeMode.Active);
            if (ImGui.SmallButton(Loc.Text("Inspector.Reveal")))
            {
                culling.Reveal(player.Identity);
                nextRefresh = 0;
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        if (shown == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Loc.Text("Inspector.NoMatches"));
        }
        ImGui.EndTable();
    }

    private static string Reason(PlayerAdmissionDecision? admission, CullingStatus status)
    {
        if (admission is not { } result)
            return status.Mode == CullingRuntimeMode.Active ? Loc.Text("Inspector.Pending") : CrowdUi.State(status);
        if (result.TemporaryReveal)
            return Loc.Text("Inspector.RevealReason");
        if (result.CutByBudget)
            return Loc.Text("Inspector.LimitReason");
        if (!result.InDrawRange)
            return Loc.Text("Inspector.GameRange");
        return result.Decision.RuleId is { } rule ? CrowdUi.RuleName(rule) : Loc.Text("Crowd.EveryoneElse");
    }
}
