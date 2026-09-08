using System;
using Dalamud.Plugin.Services;
using EventHorizon.Interop.Vfx;
using EventHorizon.Settings;
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
    internal static IFeatureDefinition CreateDefinition(
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
    ) =>
        new FeatureDefinition<TargetingMarkerSettings>(
            "targeting-me",
            "Feature.Name.TargetingMarker",
            store => store.LegacyEnabled("EnableTargetingMeMarker", false),
            (settings, save) =>
                new TargetingMarkerFeature(
                    settings,
                    save,
                    addons,
                    gameGui,
                    nameplates,
                    objects,
                    targets,
                    condition,
                    framework,
                    textures,
                    interop,
                    scanner,
                    log
                )
        );

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

internal sealed class TargetingMarkerSettings : IFeatureSettings
{
    public int Version { get; set; } = 1;
    public bool EnableTargetingMeNamePlateMarker { get; set; } = true;
    public bool EnableTargetingMeDotMarker { get; set; } = false;
    public bool EnableTargetingMeVfxMarker { get; set; } = false;
    public bool EnableTargetingMeMarkerCurrentTargetTest { get; set; } = false;
    public bool DisableTargetingMeMarkerVfxInDuty { get; set; } = true;

    [ConfigRange(-500, 500)]
    public float TargetingMeMarkerOffsetX { get; set; } = 0;

    [ConfigRange(-500, 500)]
    public float TargetingMeMarkerOffsetY { get; set; } = -45;

    [ConfigRange(0.1, 2)]
    public float TargetingMeMarkerScale { get; set; } = 1.33f;
    public byte TargetingMeMarkerOpacity { get; set; } = 255;
    public byte TargetingMeMarkerGlowOpacity { get; set; } = 255;
    public bool UseCustomTargetingMeMarkerColor { get; set; } = false;
    public byte TargetingMeMarkerColorRed { get; set; } = 255;
    public byte TargetingMeMarkerColorGreen { get; set; } = 120;
    public byte TargetingMeMarkerColorBlue { get; set; } = 40;
    public bool UseCustomTargetingMeDotColor { get; set; } = false;
    public byte TargetingMeDotColorRed { get; set; } = 255;
    public byte TargetingMeDotColorGreen { get; set; } = 0;
    public byte TargetingMeDotColorBlue { get; set; } = 0;
    public byte TargetingMeDotColorAlpha { get; set; } = 255;

    [ConfigRange(1, 20)]
    public float TargetingMeDotRadius { get; set; } = 5f;
}
