using System;
using Dalamud.Plugin.Services;
using EventHorizon.Interop.Vfx;
using EventHorizon.WorldGraphics;

namespace EventHorizon.Features.TargetingMarker;

internal sealed class TargetingMarkerFeature(
    TargetingMarkerSettings settings,
    Action save,
    IAddonLifecycle addons,
    IGameGui gameGui,
    INamePlateGui nameplates,
    IObjectTable objects,
    ITargetManager targets,
    ICondition condition,
    IFramework framework,
    ITextureProvider textures,
    IGameInteropProvider interop,
    ISigScanner scanner,
    IPluginLog log
) : Feature<TargetingMarkerSettings>(settings, save)
{
    private TargetingMarkerController? controller;

    public override void Enable(FeatureScope scope)
    {
        var dots = scope.Own(new WorldDotOverlay(gameGui));
        scope.OnDraw(dots.Draw);
        var vfx = scope.Own(new ActorVfxController(interop, scanner, log));
        controller = new TargetingMarkerController(
            addons,
            gameGui,
            nameplates,
            objects,
            targets,
            condition,
            Settings,
            framework,
            textures,
            vfx,
            dots,
            log,
            scope
        );
    }

    public override void Disable() => controller = null;

    public override void DrawSettings()
    {
        var changed = FeatureUi.Checkbox(
            "EnableTargetingMeNamePlateMarker",
            Settings.EnableTargetingMeNamePlateMarker,
            v => Settings.EnableTargetingMeNamePlateMarker = v
        );
        changed |= FeatureUi.Checkbox(
            "EnableTargetingMeDotMarker",
            Settings.EnableTargetingMeDotMarker,
            v => Settings.EnableTargetingMeDotMarker = v
        );
        changed |= FeatureUi.Checkbox(
            "EnableTargetingMeVfxMarker",
            Settings.EnableTargetingMeVfxMarker,
            v => Settings.EnableTargetingMeVfxMarker = v
        );
        changed |= FeatureUi.Checkbox(
            "DisableTargetingMeMarkerVfxInDuty",
            Settings.DisableTargetingMeMarkerVfxInDuty,
            v => Settings.DisableTargetingMeMarkerVfxInDuty = v
        );
        changed |= FeatureUi.Checkbox(
            "EnableTargetingMeMarkerCurrentTargetTest",
            Settings.EnableTargetingMeMarkerCurrentTargetTest,
            v => Settings.EnableTargetingMeMarkerCurrentTargetTest = v
        );
        changed |= FeatureUi.Slider(
            "TargetingMeMarkerOffsetX",
            Settings.TargetingMeMarkerOffsetX,
            -500,
            500,
            v => Settings.TargetingMeMarkerOffsetX = v
        );
        changed |= FeatureUi.Slider(
            "TargetingMeMarkerOffsetY",
            Settings.TargetingMeMarkerOffsetY,
            -500,
            500,
            v => Settings.TargetingMeMarkerOffsetY = v
        );
        changed |= FeatureUi.Slider(
            "TargetingMeMarkerScale",
            Settings.TargetingMeMarkerScale,
            0.1f,
            2,
            v => Settings.TargetingMeMarkerScale = v,
            "%.2f"
        );
        changed |= FeatureUi.Byte(
            "TargetingMeMarkerOpacity",
            Settings.TargetingMeMarkerOpacity,
            v => Settings.TargetingMeMarkerOpacity = v
        );
        changed |= FeatureUi.Byte(
            "TargetingMeMarkerGlowOpacity",
            Settings.TargetingMeMarkerGlowOpacity,
            v => Settings.TargetingMeMarkerGlowOpacity = v
        );
        changed |= FeatureUi.Checkbox(
            "UseCustomTargetingMeMarkerColor",
            Settings.UseCustomTargetingMeMarkerColor,
            v => Settings.UseCustomTargetingMeMarkerColor = v
        );
        if (Settings.UseCustomTargetingMeMarkerColor)
            changed |= FeatureUi.Color(
                "TargetingMeMarkerColor",
                Settings.TargetingMeMarkerColorRed,
                Settings.TargetingMeMarkerColorGreen,
                Settings.TargetingMeMarkerColorBlue,
                255,
                (r, g, b, a) =>
                {
                    Settings.TargetingMeMarkerColorRed = r;
                    Settings.TargetingMeMarkerColorGreen = g;
                    Settings.TargetingMeMarkerColorBlue = b;
                }
            );
        changed |= FeatureUi.Checkbox(
            "UseCustomTargetingMeDotColor",
            Settings.UseCustomTargetingMeDotColor,
            v => Settings.UseCustomTargetingMeDotColor = v
        );
        if (Settings.UseCustomTargetingMeDotColor)
            changed |= FeatureUi.Color(
                "TargetingMeDotColor",
                Settings.TargetingMeDotColorRed,
                Settings.TargetingMeDotColorGreen,
                Settings.TargetingMeDotColorBlue,
                Settings.TargetingMeDotColorAlpha,
                (r, g, b, a) =>
                {
                    Settings.TargetingMeDotColorRed = r;
                    Settings.TargetingMeDotColorGreen = g;
                    Settings.TargetingMeDotColorBlue = b;
                    Settings.TargetingMeDotColorAlpha = a;
                }
            );
        changed |= FeatureUi.Slider("TargetingMeDotRadius", Settings.TargetingMeDotRadius, 1, 20, v => Settings.TargetingMeDotRadius = v);
        if (changed)
        {
            Save();
            controller?.RequestRefresh();
        }
    }
}
