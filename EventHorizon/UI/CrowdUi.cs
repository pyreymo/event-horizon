using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using EventHorizon.Culling;
using EventHorizon.Localization;
using EventHorizon.Settings;

namespace EventHorizon.UI;

internal static class CrowdUi
{
    public static readonly Vector4 Accent = new(0.43f, 0.79f, 0.75f, 1f);
    public static readonly Vector4 Gold = new(0.87f, 0.75f, 0.51f, 1f);
    public static readonly Vector4 Muted = new(0.54f, 0.59f, 0.62f, 1f);
    public static readonly Vector4 Surface = new(0.115f, 0.14f, 0.16f, 1f);

    public static float S(float value) => value * ImGuiHelpers.GlobalScale;

    public static void PushStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.065f, 0.080f, 0.095f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.055f, 0.067f, 0.080f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.055f, 0.067f, 0.080f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.085f, 0.105f, 0.12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.88f, 0.91f, 0.91f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Muted);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.18f, 0.22f, 0.25f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Surface);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.17f, 0.22f, 0.24f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.20f, 0.27f, 0.29f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.16f, 0.20f, 0.23f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, new Vector4(0.11f, 0.14f, 0.16f, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.Button, Surface);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.25f, 0.27f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.22f, 0.32f, 0.34f, 1f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, new Vector4(0.64f, 0.91f, 0.87f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.13f, 0.21f, 0.23f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.18f, 0.27f, 0.29f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.22f, 0.32f, 0.34f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(S(18), S(14)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(S(8), S(6)));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(S(8), S(5)));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(S(5), S(4)));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, S(4));
    }

    public static void PopStyle()
    {
        ImGui.PopStyleVar(5);
        ImGui.PopStyleColor(24);
    }

    public static string RuleName(PlayerKeepRuleId rule) => Loc.Text($"Rule.{rule}");

    public static string State(CullingStatus status) => Loc.Text($"State.{status.Mode}");

    public static void Tooltip(string text)
    {
        if (!ImGui.IsItemHovered() && !ImGui.IsItemFocused())
            return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(S(300));
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    public static void Caption(string text) => ImGui.TextColored(Muted, text);

    public static void StateIndicator(CullingStatus status)
    {
        var color =
            status.Mode == CullingRuntimeMode.Active ? Accent
            : status.Mode == CullingRuntimeMode.Disabled ? Muted
            : Gold;
        var pos = ImGui.GetCursorScreenPos();
        var height = ImGui.GetFrameHeight();
        ImGui.GetWindowDrawList().AddCircleFilled(pos + new Vector2(S(4), height / 2), S(3), ImGui.GetColorU32(color));
        ImGui.Dummy(new Vector2(S(12), height));
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(color, State(status));
    }

    public static bool Chip(string id, string label, bool selected, Vector2 size, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? new Vector4(color.X * 0.22f, color.Y * 0.22f, color.Z * 0.22f, 1) : Surface);
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? color : Muted);
        var clicked = ImGui.Button($"{label}##{id}", size);
        ImGui.PopStyleColor(2);
        return clicked;
    }

    public static bool RuleOption(RuleTreatment treatment, bool selected, string label)
    {
        var color =
            treatment == RuleTreatment.Always ? Gold
            : treatment == RuleTreatment.Prefer ? Accent
            : Muted;
        var clicked = Chip(treatment.ToString(), string.Empty, selected, new Vector2(-1, S(25)), color);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var center = (min + max) / 2;
        var draw = ImGui.GetWindowDrawList();
        if (selected)
        {
            draw.AddCircleFilled(center, S(3), ImGui.GetColorU32(color));
            draw.AddCircle(center, S(6), ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.5f)), 16, S(1));
        }
        else
            draw.AddCircle(center, S(3), ImGui.GetColorU32(new Vector4(0.28f, 0.34f, 0.37f, 1)), 12, S(1));
        Tooltip(label);
        return clicked;
    }

    public static void Population(CullingStatus status)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var pitch = width / 25;
        var side = Math.Min(S(7), pitch - S(2));
        var height = pitch * 4;
        var draw = ImGui.GetWindowDrawList();
        // One cell per player; unused cells are empty. These are admission decisions, not draw calls.
        for (var index = 0; index < 100; index++)
        {
            var color =
                index >= status.OtherPlayerCount ? new Vector4(0.12f, 0.15f, 0.17f, 1)
                : status.Mode != CullingRuntimeMode.Active ? Muted
                : index < status.Admitted ? Accent
                : index < status.Admitted + status.Rejected ? new Vector4(0.29f, 0.35f, 0.39f, 1)
                : Gold;
            var point = pos + new Vector2(index % 25 * pitch, index / 25 * pitch);
            draw.AddRectFilled(point, point + new Vector2(side), ImGui.GetColorU32(color), S(1.5f));
        }
        ImGui.Dummy(new Vector2(width, height));
        Tooltip(Loc.Text("Panel.PopulationHelp"));
    }
}
