using EventHorizon.Settings;

namespace EventHorizon.Features.Dtr;

internal sealed class DtrSettings
{
    public int Version { get; set; } = 1;
    public bool ShowFrameRateInDtrBar { get; set; } = true;
}
