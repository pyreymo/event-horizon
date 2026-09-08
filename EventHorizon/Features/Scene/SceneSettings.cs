using EventHorizon.Settings;

namespace EventHorizon.Features.Scene;

internal sealed class SceneSettings
{
    public int Version { get; set; } = 1;
    public bool HideBgParts { get; set; } = false;
    public bool HideTerrain { get; set; } = false;
    public bool HideWater { get; set; } = false;
    public bool HideGrass { get; set; } = false;
    public bool HideAll3DScene { get; set; } = false;
}
