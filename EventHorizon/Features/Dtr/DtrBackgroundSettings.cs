using EventHorizon.Settings;

namespace EventHorizon.Features.Dtr;

internal sealed class DtrBackgroundSettings
{
    public int Version { get; set; } = 1;

    [ConfigRange(0, 80)]
    public float DtrBackgroundHorizontalPadding { get; set; } = 24f;

    [ConfigRange(0, 80)]
    public float DtrBackgroundPaddingTop { get; set; } = 10f;

    [ConfigRange(0, 80)]
    public float DtrBackgroundPaddingBottom { get; set; } = 4f;
    public byte DtrBackgroundAlpha { get; set; } = 128;
}
