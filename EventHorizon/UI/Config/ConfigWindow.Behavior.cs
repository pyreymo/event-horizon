using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using EventHorizon.Localization;
using EventHorizon.Settings;

namespace EventHorizon.UI.Config;

internal partial class ConfigWindow
{
    private void DrawBehaviorTab()
    {
        DrawCard(Loc.Text("Config.Section.DtrBar"), DrawDtrBarControls);
        DrawCard(Loc.Text("Config.Section.PlayerDisplay"), DrawPlayerDisplayControls);
        DrawCard(Loc.Text("Config.Section.TargetingMeMarker"), DrawTargetingMeMarkerControls);
        DrawCard(Loc.Text("Config.Section.LayoutGraphics"), DrawLayoutGraphicsControls);
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
        DrawTemporaryShowAllPlayersKey();

        ImGui.Spacing();

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

        if (!configuration.EnableHiddenPlayerGroundMarker)
        {
            return;
        }

        ImGui.Indent();
        var useDot = configuration.UseHiddenPlayerMarkerDot;
        if (DrawAutoFitCheckbox("UseHiddenPlayerMarkerDot", Loc.Text("Config.UseHiddenPlayerMarkerDot"), ref useDot))
        {
            configuration.UseHiddenPlayerMarkerDot = useDot;
            SaveAndRefreshWithoutRuleReset();
        }

        if (configuration.UseHiddenPlayerMarkerDot)
        {
            var color = new Vector4(
                configuration.HiddenPlayerMarkerDotColorRed / 255f,
                configuration.HiddenPlayerMarkerDotColorGreen / 255f,
                configuration.HiddenPlayerMarkerDotColorBlue / 255f,
                configuration.HiddenPlayerMarkerDotColorAlpha / 255f
            );
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.ColorEdit4($"{Loc.Text("Config.HiddenPlayerMarkerDotColor")}##HiddenPlayerMarkerDotColor", ref color))
            {
                configuration.HiddenPlayerMarkerDotColorRed = ToColorByte(color.X);
                configuration.HiddenPlayerMarkerDotColorGreen = ToColorByte(color.Y);
                configuration.HiddenPlayerMarkerDotColorBlue = ToColorByte(color.Z);
                configuration.HiddenPlayerMarkerDotColorAlpha = ToColorByte(color.W);
            }
            SaveConfigurationIfEditFinished();

            var radius = Math.Clamp(configuration.HiddenPlayerMarkerDotRadius, 1f, 20f);
            ImGui.SetNextItemWidth(-1f);
            if (
                ImGui.SliderFloat(
                    $"{Loc.Text("Config.HiddenPlayerMarkerDotSize")}##HiddenPlayerMarkerDotSize",
                    ref radius,
                    1f,
                    20f,
                    "%.0f px"
                )
            )
            {
                configuration.HiddenPlayerMarkerDotRadius = radius;
            }
            SaveConfigurationIfEditFinished();
        }
        ImGui.Unindent();
    }

    private void DrawTemporaryShowAllPlayersKey()
    {
        var enabled = configuration.EnableTemporaryShowAllPlayersShortcut;
        var enabledChanged = ImGui.Checkbox("##EnableTemporaryShowAllPlayersShortcut", ref enabled);
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Text("Config.TemporarilyShowAllPlayersKey"));
        if (ImGui.IsItemClicked())
        {
            enabled = !enabled;
            enabledChanged = true;
        }

        if (enabledChanged)
        {
            configuration.EnableTemporaryShowAllPlayersShortcut = enabled;
            if (!enabled)
            {
                capturingTemporaryShowAllPlayersShortcut = false;
                capturedTemporaryShowAllPlayersKeys.Clear();
            }
            configuration.Save();
        }

        ImGui.SameLine();
        if (!enabled)
        {
            ImGui.BeginDisabled();
        }

        var clearButtonWidth = ImGui.CalcTextSize(Loc.Text("Config.Shortcut.Clear")).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SetNextItemWidth(-clearButtonWidth - ImGui.GetStyle().ItemSpacing.X);
        var shortcutLabel = capturingTemporaryShowAllPlayersShortcut
            ? BuildShortcutLabel(capturedTemporaryShowAllPlayersKeys, Loc.Text("Config.Shortcut.Recording"))
            : BuildShortcutLabel(configuration.TemporarilyShowAllPlayersKeys, Loc.Text("Config.Shortcut.Unbound"));

        if (ImGui.Button($"{shortcutLabel}##TemporarilyShowAllPlayersKey", new Vector2(ImGui.CalcItemWidth(), 0f)))
        {
            capturingTemporaryShowAllPlayersShortcut = true;
            capturedTemporaryShowAllPlayersKeys.Clear();
        }

        ImGui.SameLine();
        if (ImGui.Button($"{Loc.Text("Config.Shortcut.Clear")}##ClearTemporarilyShowAllPlayersKey"))
        {
            capturingTemporaryShowAllPlayersShortcut = false;
            capturedTemporaryShowAllPlayersKeys.Clear();
            configuration.TemporarilyShowAllPlayersKeys.Clear();
            configuration.Save();
        }

        if (!enabled)
        {
            ImGui.EndDisabled();
        }

        DrawHelpMarker(Loc.Text("Config.TemporarilyShowAllPlayersKey.Help"));

        if (capturingTemporaryShowAllPlayersShortcut)
        {
            CaptureTemporaryShowAllPlayersShortcut();
        }
    }

    private void CaptureTemporaryShowAllPlayersShortcut()
    {
        if (Plugin.KeyState[VirtualKey.ESCAPE])
        {
            capturingTemporaryShowAllPlayersShortcut = false;
            capturedTemporaryShowAllPlayersKeys.Clear();
            return;
        }

        var anyKeyHeld = false;
        foreach (var key in Plugin.KeyState.GetValidVirtualKeys())
        {
            var keyCode = (int)key;
            if (IsIgnoredCaptureKey(key) || !Plugin.KeyState[key])
            {
                continue;
            }

            anyKeyHeld = true;
            capturedTemporaryShowAllPlayersKeys.Add(keyCode);
        }

        if (capturedTemporaryShowAllPlayersKeys.Count == 0 || anyKeyHeld)
        {
            return;
        }

        var keys = new List<int>(capturedTemporaryShowAllPlayersKeys);
        keys.Sort();
        configuration.TemporarilyShowAllPlayersKeys = keys;
        configuration.Save();
        capturingTemporaryShowAllPlayersShortcut = false;
        capturedTemporaryShowAllPlayersKeys.Clear();
    }

    private static bool IsIgnoredCaptureKey(VirtualKey key) =>
        key
            is VirtualKey.LBUTTON
                or VirtualKey.RBUTTON
                or VirtualKey.MBUTTON
                or VirtualKey.XBUTTON1
                or VirtualKey.XBUTTON2
                or VirtualKey.ESCAPE
                or VirtualKey.LSHIFT
                or VirtualKey.RSHIFT
                or VirtualKey.LCONTROL
                or VirtualKey.RCONTROL
                or VirtualKey.LMENU
                or VirtualKey.RMENU;

    private static string BuildShortcutLabel(IEnumerable<int> keys, string emptyLabel)
    {
        var labels = new List<string>();
        var addedKeys = new HashSet<int>();
        foreach (var key in keys)
        {
            if (addedKeys.Add(key))
            {
                labels.Add(GetKeyLabel((VirtualKey)key));
            }
        }

        return labels.Count == 0 ? emptyLabel : string.Join(" + ", labels);
    }

    private static string GetKeyLabel(VirtualKey key) =>
        key switch
        {
            VirtualKey.MENU => "Alt",
            VirtualKey.CONTROL => "Ctrl",
            VirtualKey.SHIFT => "Shift",
            >= VirtualKey.KEY_0 and <= VirtualKey.KEY_9 => ((char)key).ToString(),
            >= VirtualKey.A and <= VirtualKey.Z => ((char)key).ToString(),
            _ => key.ToString(),
        };

    private void DrawLayoutGraphicsControls()
    {
        ImGui.TextDisabled(Loc.Text("Config.SceneRendering.Individual"));
        DrawSceneVisibilityCheckbox(
            "HideBgParts",
            Loc.Text("Config.HideBgParts"),
            () => configuration.HideBgParts,
            value => configuration.HideBgParts = value
        );
        DrawSceneVisibilityCheckbox(
            "HideTerrain",
            Loc.Text("Config.HideTerrain"),
            () => configuration.HideTerrain,
            value => configuration.HideTerrain = value
        );
        DrawSceneVisibilityCheckbox(
            "HideWater",
            Loc.Text("Config.HideWater"),
            () => configuration.HideWater,
            value => configuration.HideWater = value
        );
        DrawSceneVisibilityCheckbox(
            "HideGrass",
            Loc.Text("Config.HideGrass"),
            () => configuration.HideGrass,
            value => configuration.HideGrass = value
        );

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled(Loc.Text("Config.SceneRendering.All"));
        DrawSceneVisibilityCheckbox(
            "HideAll3DScene",
            Loc.Text("Config.HideAll3DScene"),
            () => configuration.HideAll3DScene,
            value => configuration.HideAll3DScene = value
        );
        DrawHelpMarker(Loc.Text("Config.HideAll3DScene.Help"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled(Loc.Text("Config.GBufferProbe.Section"));
        DrawSceneVisibilityCheckbox(
            "EnableGBufferProbe",
            Loc.Text("Config.EnableGBufferProbe"),
            () => configuration.EnableGBufferProbe,
            value => configuration.EnableGBufferProbe = value
        );
        DrawHelpMarker(Loc.Text("Config.EnableGBufferProbe.Help"));

        if (configuration.EnableGBufferProbe)
        {
            ImGui.Indent();
            DrawGBufferProbeMode();

            ImGui.TextUnformatted(Loc.Text("Config.GBufferProbeCandidateExitOrdinal"));
            var ordinal = configuration.GBufferProbeCandidateExitOrdinal;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputInt("##GBufferProbeCandidateExitOrdinal", ref ordinal))
            {
                configuration.GBufferProbeCandidateExitOrdinal = Math.Clamp(ordinal, 0, 31);
                configuration.Save();
            }
            DrawHelpMarker(Loc.Text("Config.GBufferProbeCandidateExitOrdinal.Help"));
            ImGui.Unindent();
        }
    }

    private void DrawGBufferProbeMode()
    {
        ImGui.TextUnformatted(Loc.Text("Config.GBufferProbeMode"));
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##GBufferProbeMode", GetGBufferProbeModeLabel(configuration.GBufferProbeMode)))
        {
            foreach (var mode in Enum.GetValues<GBufferProbeMode>())
            {
                if (ImGui.Selectable(GetGBufferProbeModeLabel(mode), mode == configuration.GBufferProbeMode))
                {
                    configuration.GBufferProbeMode = mode;
                    configuration.Save();
                }
            }

            ImGui.EndCombo();
        }

        DrawHelpMarker(Loc.Text("Config.GBufferProbeMode.Help"));
    }

    private static string GetGBufferProbeModeLabel(GBufferProbeMode mode) =>
        mode switch
        {
            GBufferProbeMode.NoOp => Loc.Text("Config.GBufferProbeMode.NoOp"),
            GBufferProbeMode.DepthOnly => Loc.Text("Config.GBufferProbeMode.DepthOnly"),
            GBufferProbeMode.Target0Rgb => Loc.Text("Config.GBufferProbeMode.Target0Rgb"),
            GBufferProbeMode.Target2Rgb => Loc.Text("Config.GBufferProbeMode.Target2Rgb"),
            GBufferProbeMode.WorldTriangle => Loc.Text("Config.GBufferProbeMode.WorldTriangle"),
            _ => mode.ToString(),
        };

    private void DrawSceneVisibilityCheckbox(string id, string label, Func<bool> getValue, Action<bool> setValue)
    {
        var value = getValue();
        if (DrawAutoFitCheckbox(id, label, ref value))
        {
            setValue(value);
            configuration.Save();
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
        ImGui.Spacing();
        DrawTargetingMeDotMarkerSection();
        ImGui.Spacing();
        DrawTargetingMeVfxMarkerSection();
        ImGui.Spacing();
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

    private void DrawTargetingMeDotMarkerSection()
    {
        var enableDotMarker = configuration.EnableTargetingMeDotMarker;
        if (DrawAutoFitCheckbox("EnableTargetingMeDotMarker", Loc.Text("Config.EnableTargetingMeDotMarker"), ref enableDotMarker))
        {
            configuration.EnableTargetingMeDotMarker = enableDotMarker;
            SaveAndRequestTargetingMeMarkerRefresh();
        }

        if (!configuration.EnableTargetingMeDotMarker)
        {
            return;
        }

        ImGui.Spacing();
        if (ImGui.TreeNodeEx($"{Loc.Text("Config.Section.MarkerStyle")}##TargetingMeDotMarkerStyle", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            if (BeginBehaviorControlTable("TargetingMeDotMarkerStyleTable"))
            {
                var useCustomColor = configuration.UseCustomTargetingMeDotColor;
                if (
                    DrawBehaviorCheckbox(
                        "UseCustomTargetingMeDotColor",
                        Loc.Text("Config.UseCustomTargetingMeDotColor"),
                        ref useCustomColor
                    )
                )
                {
                    configuration.UseCustomTargetingMeDotColor = useCustomColor;
                    SaveAndRequestTargetingMeMarkerRefresh();
                }

                if (configuration.UseCustomTargetingMeDotColor)
                {
                    var color = new Vector4(
                        configuration.TargetingMeDotColorRed / 255f,
                        configuration.TargetingMeDotColorGreen / 255f,
                        configuration.TargetingMeDotColorBlue / 255f,
                        configuration.TargetingMeDotColorAlpha / 255f
                    );
                    DrawBehaviorControlLabel(Loc.Text("Config.TargetingMeDotColor"));
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.ColorEdit4("##TargetingMeDotColor", ref color))
                    {
                        configuration.TargetingMeDotColorRed = ToColorByte(color.X);
                        configuration.TargetingMeDotColorGreen = ToColorByte(color.Y);
                        configuration.TargetingMeDotColorBlue = ToColorByte(color.Z);
                        configuration.TargetingMeDotColorAlpha = ToColorByte(color.W);
                    }
                    SaveConfigurationIfEditFinished();
                }

                var radius = Math.Clamp(configuration.TargetingMeDotRadius, 1f, 20f);
                if (DrawBehaviorSliderFloat("TargetingMeDotRadius", Loc.Text("Config.TargetingMeDotSize"), ref radius, 1f, 20f, "%.0f px"))
                {
                    configuration.TargetingMeDotRadius = radius;
                }
                SaveConfigurationIfEditFinished();

                ImGui.EndTable();
            }

            ImGui.TreePop();
        }
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
        SaveConfigurationIfEditFinished();

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
        SaveConfigurationIfEditFinished();

        var top = Math.Clamp(configuration.DtrBackgroundPaddingTop, 0f, 80f);
        if (DrawBehaviorSliderFloat("DtrBackgroundPaddingTop", Loc.Text("Config.DtrBackgroundPaddingTop"), ref top, 0f, 80f, "%.0f"))
        {
            configuration.DtrBackgroundPaddingTop = top;
            RefreshDtrBackground();
        }
        SaveConfigurationIfEditFinished();

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
        SaveConfigurationIfEditFinished();

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
        ImGui.TextUnformatted(label);
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

    private void SaveConfigurationIfEditFinished()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            configuration.Save();
        }
    }
}
