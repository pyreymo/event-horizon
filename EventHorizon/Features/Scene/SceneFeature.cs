using System;
using Dalamud.Plugin.Services;

namespace EventHorizon.Features.Scene;

internal sealed class SceneFeature(SceneSettings settings, Action save, IGameInteropProvider interop)
    : Feature<SceneSettings>(settings, save)
{
    public override void Enable(FeatureScope scope)
    {
        var controller = new SceneVisibilityController(interop, Settings, scope);
        controller.Enable();
        scope.OnUpdate(controller.Update);
    }

    public override void DrawSettings()
    {
        var changed = FeatureUi.Checkbox("HideBgParts", Settings.HideBgParts, v => Settings.HideBgParts = v);
        changed |= FeatureUi.Checkbox("HideTerrain", Settings.HideTerrain, v => Settings.HideTerrain = v);
        changed |= FeatureUi.Checkbox("HideWater", Settings.HideWater, v => Settings.HideWater = v);
        changed |= FeatureUi.Checkbox("HideGrass", Settings.HideGrass, v => Settings.HideGrass = v);
        changed |= FeatureUi.Checkbox("HideAll3DScene", Settings.HideAll3DScene, v => Settings.HideAll3DScene = v);
        if (changed)
            Save();
    }
}
