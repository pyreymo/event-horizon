using System;
using Dalamud.Bindings.ImGui;
using EventHorizon.Localization;
using EventHorizon.Settings;

namespace EventHorizon.UI.Config;

public partial class ConfigWindow
{
    private void DrawBehaviorTab()
    {
        DrawCard(
            Loc.Text("Config.Tab.Behavior"),
            () =>
            {
                var showDtrBar = configuration.ShowDtrBar;
                if (ImGui.Checkbox(Loc.Text("Config.ShowDtrBar"), ref showDtrBar))
                {
                    configuration.ShowDtrBar = showDtrBar;
                    SaveAndRefreshDtrBar();
                }

                var showFrameRateInDtrBar = configuration.ShowFrameRateInDtrBar;
                if (ImGui.Checkbox(Loc.Text("Config.ShowFrameRateInDtrBar"), ref showFrameRateInDtrBar))
                {
                    configuration.ShowFrameRateInDtrBar = showFrameRateInDtrBar;
                    SaveAndRefreshDtrBar();
                }

                var enableFadeTransitions = configuration.EnableFadeTransitions;
                if (ImGui.Checkbox(Loc.Text("Config.EnableFadeTransitions"), ref enableFadeTransitions))
                {
                    configuration.EnableFadeTransitions = enableFadeTransitions;
                    SaveAndRefresh();
                }
            }
        );

        DrawExperimentalFeatures();
    }

    private void DrawExperimentalFeatures()
    {
        DrawCard(
            Loc.Text("Config.Section.Experimental"),
            () =>
            {
                var enableDtrBackground = configuration.EnableDtrBackground;
                if (ImGui.Checkbox(Loc.Text("Config.EnableDtrBackground"), ref enableDtrBackground))
                {
                    configuration.EnableDtrBackground = enableDtrBackground;
                    SaveAndRefreshDtrBackground();
                }

                DrawHelpText(Loc.Text("Config.EnableDtrBackground.Help"));

                if (configuration.EnableDtrBackground)
                {
                    ImGui.Indent();
                    DrawDtrBackgroundStyleControls();
                    ImGui.Unindent();
                }

                var enableTargetingMeMarker = configuration.EnableTargetingMeMarker;
                if (ImGui.Checkbox(Loc.Text("Config.EnableTargetingMeMarker"), ref enableTargetingMeMarker))
                {
                    configuration.EnableTargetingMeMarker = enableTargetingMeMarker;
                    SaveAndRequestTargetingMeMarkerRefresh();
                }

                DrawHelpText(Loc.Text("Config.EnableTargetingMeMarker.Help"));

                if (configuration.EnableTargetingMeMarker)
                {
                    ImGui.Indent();
                    var enableTargetingMeMarkerCurrentTargetTest = configuration.EnableTargetingMeMarkerCurrentTargetTest;

                    if (
                        ImGui.Checkbox(
                            Loc.Text("Config.EnableTargetingMeMarkerCurrentTargetTest"),
                            ref enableTargetingMeMarkerCurrentTargetTest
                        )
                    )
                    {
                        configuration.EnableTargetingMeMarkerCurrentTargetTest = enableTargetingMeMarkerCurrentTargetTest;
                        SaveAndRequestTargetingMeMarkerRefresh();
                    }

                    DrawHelpText(Loc.Text("Config.EnableTargetingMeMarkerCurrentTargetTest.Help"));

                    DrawTargetingMeMarkerStyleControls();
                    ImGui.Unindent();
                }
            }
        );
    }

    private void DrawDtrBackgroundStyleControls()
    {
        var alpha = configuration.DtrBackgroundAlpha;
        var alphaValue = (int)alpha;
        if (ImGui.SliderInt(Loc.Text("Config.DtrBackgroundAlpha"), ref alphaValue, 0, 255))
        {
            configuration.DtrBackgroundAlpha = (byte)alphaValue;
            RefreshDtrBackground();
        }
        SaveDtrBackgroundIfEditFinished();

        var horizontalPadding = Math.Clamp(configuration.DtrBackgroundHorizontalPadding, 0f, 80f);
        if (ImGui.SliderFloat(Loc.Text("Config.DtrBackgroundHorizontalPadding"), ref horizontalPadding, 0f, 80f, "%.0f"))
        {
            configuration.DtrBackgroundHorizontalPadding = horizontalPadding;
            RefreshDtrBackground();
        }
        SaveDtrBackgroundIfEditFinished();

        var top = configuration.DtrBackgroundPaddingTop;
        if (ImGui.SliderFloat(Loc.Text("Config.DtrBackgroundPaddingTop"), ref top, 0f, 80f, "%.0f"))
        {
            configuration.DtrBackgroundPaddingTop = top;
            RefreshDtrBackground();
        }
        SaveDtrBackgroundIfEditFinished();

        var bottom = configuration.DtrBackgroundPaddingBottom;
        if (ImGui.SliderFloat(Loc.Text("Config.DtrBackgroundPaddingBottom"), ref bottom, 0f, 80f, "%.0f"))
        {
            configuration.DtrBackgroundPaddingBottom = bottom;
            RefreshDtrBackground();
        }
        SaveDtrBackgroundIfEditFinished();
    }

    private void DrawTargetingMeMarkerStyleControls()
    {
        AddVerticalSpace(6f);

        var visualStyle = configuration.TargetingMeMarkerVisualStyle;
        if (ImGui.BeginCombo(Loc.Text("Config.TargetingMeMarkerVisualStyle"), GetTargetingMeMarkerVisualStyleText(visualStyle)))
        {
            DrawTargetingMeMarkerVisualStyleOption(TargetingMeMarkerVisualStyle.AlertEye, visualStyle);
            ImGui.EndCombo();
        }

        DrawNativeTargetingMeMarkerStyleControls();

        var offsetX = Math.Clamp(configuration.TargetingMeMarkerOffsetX, -500f, 500f);
        if (ImGui.SliderFloat(Loc.Text("Config.TargetingMeMarkerOffsetX"), ref offsetX, -500f, 500f, "%.0f"))
        {
            configuration.TargetingMeMarkerOffsetX = offsetX;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        var offsetY = Math.Clamp(configuration.TargetingMeMarkerOffsetY, -500f, 500f);
        if (ImGui.SliderFloat(Loc.Text("Config.TargetingMeMarkerOffsetY"), ref offsetY, -500f, 500f, "%.0f"))
        {
            configuration.TargetingMeMarkerOffsetY = offsetY;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        var opacity = configuration.TargetingMeMarkerOpacity;
        var opacityValue = (int)opacity;
        if (ImGui.SliderInt(Loc.Text("Config.TargetingMeMarkerOpacity"), ref opacityValue, 0, 255))
        {
            configuration.TargetingMeMarkerOpacity = (byte)opacityValue;
            SaveAndRequestTargetingMeMarkerRefresh();
        }
    }

    private void DrawTargetingMeMarkerVisualStyleOption(TargetingMeMarkerVisualStyle option, TargetingMeMarkerVisualStyle current)
    {
        if (ImGui.Selectable(GetTargetingMeMarkerVisualStyleText(option), option == current))
        {
            configuration.TargetingMeMarkerVisualStyle = option;
            SaveAndRequestTargetingMeMarkerRefresh();
        }
    }

    private static string GetTargetingMeMarkerVisualStyleText(TargetingMeMarkerVisualStyle visualStyle)
    {
        return visualStyle switch
        {
            TargetingMeMarkerVisualStyle.AlertEye => Loc.Text("Config.TargetingMeMarkerVisualStyle.AlertEye"),
            _ => visualStyle.ToString(),
        };
    }

    private void DrawNativeTargetingMeMarkerStyleControls()
    {
        var scale = Math.Clamp(configuration.TargetingMeMarkerScale, 0.1f, 2.0f);
        if (ImGui.SliderFloat(Loc.Text("Config.TargetingMeMarkerScale"), ref scale, 0.1f, 2.0f, "%.2f"))
        {
            configuration.TargetingMeMarkerScale = scale;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        var glowOpacity = configuration.TargetingMeMarkerGlowOpacity;
        var glowOpacityValue = (int)glowOpacity;
        if (ImGui.SliderInt(Loc.Text("Config.TargetingMeMarkerGlowOpacity"), ref glowOpacityValue, 0, 255))
        {
            configuration.TargetingMeMarkerGlowOpacity = (byte)glowOpacityValue;
            SaveAndRequestTargetingMeMarkerRefresh();
        }
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
