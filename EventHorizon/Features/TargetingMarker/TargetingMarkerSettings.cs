using EventHorizon.Settings;

namespace EventHorizon.Features.TargetingMarker;

internal sealed class TargetingMarkerSettings
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
