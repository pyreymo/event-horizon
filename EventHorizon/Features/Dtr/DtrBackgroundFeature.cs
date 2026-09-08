using System;
using Dalamud.Plugin.Services;

namespace EventHorizon.Features.Dtr;

internal sealed class DtrBackgroundFeature(
    DtrBackgroundSettings settings,
    Action save,
    IAddonLifecycle addons,
    IGameGui gameGui,
    IFramework framework,
    IClientState clientState
) : Feature<DtrBackgroundSettings>(settings, save)
{
    public override void Enable(FeatureScope scope) => _ = new DtrBackground(addons, gameGui, framework, clientState, Settings, scope);

    public override void DrawSettings()
    {
        var changed = FeatureUi.Slider(
            "DtrBackgroundHorizontalPadding",
            Settings.DtrBackgroundHorizontalPadding,
            0,
            80,
            v => Settings.DtrBackgroundHorizontalPadding = v
        );
        changed |= FeatureUi.Slider(
            "DtrBackgroundPaddingTop",
            Settings.DtrBackgroundPaddingTop,
            0,
            80,
            v => Settings.DtrBackgroundPaddingTop = v
        );
        changed |= FeatureUi.Slider(
            "DtrBackgroundPaddingBottom",
            Settings.DtrBackgroundPaddingBottom,
            0,
            80,
            v => Settings.DtrBackgroundPaddingBottom = v
        );
        changed |= FeatureUi.Byte("DtrBackgroundAlpha", Settings.DtrBackgroundAlpha, v => Settings.DtrBackgroundAlpha = v);
        if (changed)
            Save();
    }
}
