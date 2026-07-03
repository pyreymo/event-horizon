using System;
using Dalamud.Bindings.ImGui;
using EventHorizon.Localization;

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

                if (!configuration.EnableDtrBackground)
                {
                    return;
                }

                AddVerticalSpace(6f);

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
        );
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
