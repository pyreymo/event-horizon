using EventHorizon.Settings;

namespace EventHorizon.Features.HiddenPlayerMarker;

internal sealed class HiddenPlayerMarkerSettings
{
    public int Version { get; set; } = 1;
    public bool UseHiddenPlayerMarkerDot { get; set; } = false;
    public byte HiddenPlayerMarkerDotColorRed { get; set; } = 124;
    public byte HiddenPlayerMarkerDotColorGreen { get; set; } = 89;
    public byte HiddenPlayerMarkerDotColorBlue { get; set; } = 158;
    public byte HiddenPlayerMarkerDotColorAlpha { get; set; } = 180;

    [ConfigRange(1, 20)]
    public float HiddenPlayerMarkerDotRadius { get; set; } = 5f;
}
