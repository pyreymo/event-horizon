using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using EventHorizon.Localization;

namespace EventHorizon.UI.Config;

public partial class ConfigWindow
{
    private void DrawBehaviorTab()
    {
        DrawCard(Loc.Text("Config.Section.DtrBar"), DrawDtrBarControls);
        DrawCard(Loc.Text("Config.Section.PlayerDisplay"), DrawPlayerDisplayControls);
        DrawCard(Loc.Text("Config.Section.TargetingMeMarker"), DrawTargetingMeMarkerControls);
        DrawCard(Loc.Text("Config.Section.Debug"), DrawDebugControls);
    }

    private void DrawDtrBarControls()
    {
        var showDtrBar = configuration.ShowDtrBar;
        if (DrawAutoFitCheckbox("ShowDtrBar", Loc.Text("Config.ShowDtrBar"), ref showDtrBar))
        {
            configuration.ShowDtrBar = showDtrBar;
            SaveAndRefreshDtrBar();
        }

        if (!configuration.ShowDtrBar)
        {
            return;
        }

        ImGui.Indent();

        var showFrameRateInDtrBar = configuration.ShowFrameRateInDtrBar;
        if (DrawAutoFitCheckbox("ShowFrameRateInDtrBar", Loc.Text("Config.ShowFrameRateInDtrBar"), ref showFrameRateInDtrBar))
        {
            configuration.ShowFrameRateInDtrBar = showFrameRateInDtrBar;
            SaveAndRefreshDtrBar();
        }

        DrawDtrBackgroundControls();

        ImGui.Unindent();
    }

    private void DrawDtrBackgroundControls()
    {
        var enableDtrBackground = configuration.EnableDtrBackground;
        if (DrawAutoFitCheckbox("EnableDtrBackground", Loc.Text("Config.EnableDtrBackground"), ref enableDtrBackground))
        {
            configuration.EnableDtrBackground = enableDtrBackground;
            SaveAndRefreshDtrBackground();
        }

        if (!configuration.EnableDtrBackground)
        {
            return;
        }

        ImGui.Spacing();
        if (ImGui.TreeNodeEx($"{Loc.Text("Config.Section.DtrBackgroundStyle")}##DtrBackgroundStyle", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            DrawDtrBackgroundStyleControls();
            ImGui.TreePop();
        }
    }

    private void DrawPlayerDisplayControls()
    {
        var enableFadeTransitions = configuration.EnableFadeTransitions;
        if (DrawAutoFitCheckbox("EnableFadeTransitions", Loc.Text("Config.EnableFadeTransitions"), ref enableFadeTransitions))
        {
            configuration.EnableFadeTransitions = enableFadeTransitions;
            SaveAndRefresh();
        }

        var enableHiddenPlayerGroundMarker = configuration.EnableHiddenPlayerGroundMarker;
        if (
            DrawAutoFitCheckbox(
                "EnableHiddenPlayerGroundMarker",
                Loc.Text("Config.EnableHiddenPlayerGroundMarker"),
                ref enableHiddenPlayerGroundMarker
            )
        )
        {
            configuration.EnableHiddenPlayerGroundMarker = enableHiddenPlayerGroundMarker;
            SaveAndRefreshWithoutRuleReset();
        }
    }

    private void DrawTargetingMeMarkerControls()
    {
        var enableTargetingMeMarker = configuration.EnableTargetingMeMarker;
        if (DrawAutoFitCheckbox("EnableTargetingMeMarker", Loc.Text("Config.EnableTargetingMeMarker"), ref enableTargetingMeMarker))
        {
            configuration.EnableTargetingMeMarker = enableTargetingMeMarker;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        if (!configuration.EnableTargetingMeMarker)
        {
            return;
        }

        ImGui.Indent();

        DrawTargetingMeNamePlateMarkerSection();
        DrawTargetingMeVfxMarkerSection();
        DrawTargetingMeMarkerTestControls();

        ImGui.Unindent();
    }

    private void DrawTargetingMeMarkerTestControls()
    {
        var enableTargetingMeMarkerCurrentTargetTest = configuration.EnableTargetingMeMarkerCurrentTargetTest;
        if (
            DrawAutoFitCheckbox(
                "EnableTargetingMeMarkerCurrentTargetTest",
                Loc.Text("Config.EnableTargetingMeMarkerCurrentTargetTest"),
                ref enableTargetingMeMarkerCurrentTargetTest
            )
        )
        {
            configuration.EnableTargetingMeMarkerCurrentTargetTest = enableTargetingMeMarkerCurrentTargetTest;
            SaveAndRequestTargetingMeMarkerRefresh();
        }
    }

    private void DrawDebugControls()
    {
        var buttonWidth = Math.Max(1f, (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f);
        if (ImGui.Button(Loc.Text("Config.Debug.ScrollChatUpFiveLines"), new Vector2(buttonWidth, 0f)))
        {
            plugin.ScrollChatLogLines(-5);
        }

        ImGui.SameLine();

        if (ImGui.Button(Loc.Text("Config.Debug.ScrollChatDownFiveLines"), new Vector2(buttonWidth, 0f)))
        {
            plugin.ScrollChatLogLines(5);
        }
    }

    private void DrawTargetingMeNamePlateMarkerSection()
    {
        var enableNamePlateMarker = configuration.EnableTargetingMeNamePlateMarker;
        if (
            DrawAutoFitCheckbox(
                "EnableTargetingMeNamePlateMarker",
                Loc.Text("Config.EnableTargetingMeNamePlateMarker"),
                ref enableNamePlateMarker
            )
        )
        {
            configuration.EnableTargetingMeNamePlateMarker = enableNamePlateMarker;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        if (!configuration.EnableTargetingMeNamePlateMarker)
        {
            return;
        }

        ImGui.Spacing();
        if (
            ImGui.TreeNodeEx(
                $"{Loc.Text("Config.Section.MarkerStyle")}##TargetingMeNamePlateMarkerStyle",
                ImGuiTreeNodeFlags.SpanAvailWidth
            )
        )
        {
            DrawTargetingMeNamePlateMarkerControls();
            ImGui.TreePop();
        }
    }

    private void DrawTargetingMeVfxMarkerSection()
    {
        var enableVfxMarker = configuration.EnableTargetingMeVfxMarker;
        if (DrawAutoFitCheckbox("EnableTargetingMeVfxMarker", Loc.Text("Config.EnableTargetingMeVfxMarker"), ref enableVfxMarker))
        {
            configuration.EnableTargetingMeVfxMarker = enableVfxMarker;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        if (!configuration.EnableTargetingMeVfxMarker)
        {
            return;
        }

        ImGui.Indent();

        var disableInDuty = configuration.DisableTargetingMeMarkerVfxInDuty;
        if (
            DrawAutoFitCheckbox(
                "DisableTargetingMeMarkerVfxInDuty",
                Loc.Text("Config.DisableTargetingMeMarkerVfxInDuty"),
                ref disableInDuty
            )
        )
        {
            configuration.DisableTargetingMeMarkerVfxInDuty = disableInDuty;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        ImGui.Unindent();
    }

    private void DrawDtrBackgroundStyleControls()
    {
        if (!BeginBehaviorControlTable("DtrBackgroundStyleTable"))
        {
            return;
        }

        var alphaValue = (int)configuration.DtrBackgroundAlpha;
        if (DrawBehaviorSliderInt("DtrBackgroundAlpha", Loc.Text("Config.DtrBackgroundAlpha"), ref alphaValue, 0, 255))
        {
            configuration.DtrBackgroundAlpha = (byte)alphaValue;
            RefreshDtrBackground();
        }
        SaveDtrBackgroundIfEditFinished();

        var horizontalPadding = Math.Clamp(configuration.DtrBackgroundHorizontalPadding, 0f, 80f);
        if (
            DrawBehaviorSliderFloat(
                "DtrBackgroundHorizontalPadding",
                Loc.Text("Config.DtrBackgroundHorizontalPadding"),
                ref horizontalPadding,
                0f,
                80f,
                "%.0f"
            )
        )
        {
            configuration.DtrBackgroundHorizontalPadding = horizontalPadding;
            RefreshDtrBackground();
        }
        SaveDtrBackgroundIfEditFinished();

        var top = Math.Clamp(configuration.DtrBackgroundPaddingTop, 0f, 80f);
        if (DrawBehaviorSliderFloat("DtrBackgroundPaddingTop", Loc.Text("Config.DtrBackgroundPaddingTop"), ref top, 0f, 80f, "%.0f"))
        {
            configuration.DtrBackgroundPaddingTop = top;
            RefreshDtrBackground();
        }
        SaveDtrBackgroundIfEditFinished();

        var bottom = Math.Clamp(configuration.DtrBackgroundPaddingBottom, 0f, 80f);
        if (
            DrawBehaviorSliderFloat(
                "DtrBackgroundPaddingBottom",
                Loc.Text("Config.DtrBackgroundPaddingBottom"),
                ref bottom,
                0f,
                80f,
                "%.0f"
            )
        )
        {
            configuration.DtrBackgroundPaddingBottom = bottom;
            RefreshDtrBackground();
        }
        SaveDtrBackgroundIfEditFinished();

        ImGui.EndTable();
    }

    private void DrawTargetingMeNamePlateMarkerControls()
    {
        if (!BeginBehaviorControlTable("TargetingMeNamePlateMarkerStyleTable"))
        {
            return;
        }

        var scale = Math.Clamp(configuration.TargetingMeMarkerScale, 0.1f, 2.0f);
        if (DrawBehaviorSliderFloat("TargetingMeMarkerScale", Loc.Text("Config.TargetingMeMarkerScale"), ref scale, 0.1f, 2.0f, "%.2f"))
        {
            configuration.TargetingMeMarkerScale = scale;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        var glowOpacityValue = (int)configuration.TargetingMeMarkerGlowOpacity;
        if (
            DrawBehaviorSliderInt(
                "TargetingMeMarkerGlowOpacity",
                Loc.Text("Config.TargetingMeMarkerGlowOpacity"),
                ref glowOpacityValue,
                0,
                255
            )
        )
        {
            configuration.TargetingMeMarkerGlowOpacity = (byte)glowOpacityValue;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        var offsetX = Math.Clamp(configuration.TargetingMeMarkerOffsetX, -500f, 500f);
        if (
            DrawBehaviorSliderFloat(
                "TargetingMeMarkerOffsetX",
                Loc.Text("Config.TargetingMeMarkerOffsetX"),
                ref offsetX,
                -500f,
                500f,
                "%.0f"
            )
        )
        {
            configuration.TargetingMeMarkerOffsetX = offsetX;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        var offsetY = Math.Clamp(configuration.TargetingMeMarkerOffsetY, -500f, 500f);
        if (
            DrawBehaviorSliderFloat(
                "TargetingMeMarkerOffsetY",
                Loc.Text("Config.TargetingMeMarkerOffsetY"),
                ref offsetY,
                -500f,
                500f,
                "%.0f"
            )
        )
        {
            configuration.TargetingMeMarkerOffsetY = offsetY;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        var opacityValue = (int)configuration.TargetingMeMarkerOpacity;
        if (DrawBehaviorSliderInt("TargetingMeMarkerOpacity", Loc.Text("Config.TargetingMeMarkerOpacity"), ref opacityValue, 0, 255))
        {
            configuration.TargetingMeMarkerOpacity = (byte)opacityValue;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        var useCustomColor = configuration.UseCustomTargetingMeMarkerColor;
        if (DrawBehaviorCheckbox("UseCustomTargetingMeMarkerColor", Loc.Text("Config.UseCustomTargetingMeMarkerColor"), ref useCustomColor))
        {
            configuration.UseCustomTargetingMeMarkerColor = useCustomColor;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        if (configuration.UseCustomTargetingMeMarkerColor)
        {
            var color = new Vector3(
                configuration.TargetingMeMarkerColorRed / 255f,
                configuration.TargetingMeMarkerColorGreen / 255f,
                configuration.TargetingMeMarkerColorBlue / 255f
            );
            if (DrawBehaviorColorEdit3("TargetingMeMarkerColor", Loc.Text("Config.TargetingMeMarkerColor"), ref color))
            {
                configuration.TargetingMeMarkerColorRed = ToColorByte(color.X);
                configuration.TargetingMeMarkerColorGreen = ToColorByte(color.Y);
                configuration.TargetingMeMarkerColorBlue = ToColorByte(color.Z);
                SaveAndRequestTargetingMeMarkerRefresh();
            }
        }

        ImGui.EndTable();
    }

    private static bool BeginBehaviorControlTable(string id)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
        {
            return false;
        }

        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Control", ImGuiTableColumnFlags.WidthStretch);
        return true;
    }

    private static bool DrawBehaviorSliderInt(string id, string label, ref int value, int min, int max, string format = "%d")
    {
        DrawBehaviorControlLabel(label);
        ImGui.SetNextItemWidth(-1f);
        return ImGui.SliderInt($"##{id}", ref value, min, max, format);
    }

    private static bool DrawBehaviorSliderFloat(string id, string label, ref float value, float min, float max, string format)
    {
        DrawBehaviorControlLabel(label);
        ImGui.SetNextItemWidth(-1f);
        return ImGui.SliderFloat($"##{id}", ref value, min, max, format);
    }

    private static bool DrawBehaviorCheckbox(string id, string label, ref bool value)
    {
        DrawBehaviorControlLabel(label);
        return ImGui.Checkbox($"##{id}", ref value);
    }

    private static bool DrawBehaviorColorEdit3(string id, string label, ref Vector3 color)
    {
        DrawBehaviorControlLabel(label);
        ImGui.SetNextItemWidth(-1f);
        return ImGui.ColorEdit3($"##{id}", ref color);
    }

    private static void DrawBehaviorControlLabel(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        DrawAutoFitText(label);
        ImGui.TableSetColumnIndex(1);
    }

    private static byte ToColorByte(float value)
    {
        return (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    }

    private void SaveAndRefreshDtrBar()
    {
        configuration.Save();
        plugin.RefreshDtrBar();
    }

    private void SaveAndRefreshDtrBackground()
    {
        configuration.Save();
        plugin.RefreshDtrBackground();
    }

    private void SaveAndRequestTargetingMeMarkerRefresh()
    {
        configuration.Save();
        plugin.RequestTargetingMeMarkerRefresh();
    }

    private void RefreshDtrBackground()
    {
        plugin.RefreshDtrBackground();
    }

    private void SaveDtrBackgroundIfEditFinished()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            configuration.Save();
        }
    }
}
