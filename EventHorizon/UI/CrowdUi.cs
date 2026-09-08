using System.Numerics;
using Dalamud.Bindings.ImGui;
using EventHorizon.Culling;
using EventHorizon.Localization;
using EventHorizon.Settings;

namespace EventHorizon.UI;

internal static class CrowdUi
{
    public static readonly Vector4 Accent = new(0.72f, 0.64f, 0.87f, 1f);

    public static void PushStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.085f, 0.085f, 0.10f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.91f, 0.90f, 0.88f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, new Vector4(0.59f, 0.59f, 0.64f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.16f, 0.15f, 0.19f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.25f, 0.22f, 0.31f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.30f, 0.26f, 0.38f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.27f, 0.25f, 0.30f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, new Vector4(0.14f, 0.13f, 0.17f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.23f, 0.20f, 0.30f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.34f, 0.29f, 0.44f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.42f, 0.35f, 0.53f, 1f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, new Vector4(0.83f, 0.76f, 0.96f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.22f, 0.19f, 0.28f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.29f, 0.25f, 0.36f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.35f, 0.29f, 0.44f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(24, 22));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 7));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    public static void PopStyle()
    {
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(17);
    }

    public static string RuleName(PlayerKeepRuleId rule) => Loc.Text($"Rule.{rule}");

    public static string State(CullingStatus status) => Loc.Text($"State.{status.Mode}");

    public static void Hint(string text)
    {
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }

    public static void Section(string key)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Accent, Loc.Text(key));
    }
}
