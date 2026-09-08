using System;
using System.Collections.Generic;
using EventHorizon.Application;
using EventHorizon.Features.Dtr;
using EventHorizon.Features.HiddenPlayerMarker;
using EventHorizon.Features.Preview;
using EventHorizon.Features.Scene;
using EventHorizon.Features.TargetingMarker;

namespace EventHorizon.Features;

// This is the composition root for built-in features. Core never references this catalog.
internal static class FeatureCatalog
{
    public static IEnumerable<FeatureRegistration> Create(
        FeatureConfigStore store,
        ICullingReader reader,
        ICullingCommands commands,
        Action openSettings,
        Action<FeatureScope, string, Action> registerCommand,
        bool safeMode
    )
    {
        bool Enabled(string legacyKey, bool fallback) => !safeMode && store.LegacyEnabled(legacyKey, fallback);
        return
        [
            Register<DtrSettings>(
                store,
                "dtr",
                "Feature.Name.Dtr",
                Enabled("ShowDtrBar", true),
                (settings, save) => new DtrFeature(settings, save, Plugin.DtrBar, reader, commands, openSettings)
            ),
            Register<DtrBackgroundSettings>(
                store,
                "dtr-background",
                "Feature.Name.DtrBackground",
                Enabled("EnableDtrBackground", false),
                (settings, save) =>
                    new DtrBackgroundFeature(settings, save, Plugin.AddonLifecycle, Plugin.GameGui, Plugin.Framework, Plugin.ClientState)
            ),
            Register<TargetingMarkerSettings>(
                store,
                "targeting-me",
                "Feature.Name.TargetingMarker",
                Enabled("EnableTargetingMeMarker", true),
                (settings, save) =>
                    new TargetingMarkerFeature(
                        settings,
                        save,
                        Plugin.AddonLifecycle,
                        Plugin.GameGui,
                        Plugin.NamePlateGui,
                        Plugin.ObjectTable,
                        Plugin.TargetManager,
                        Plugin.Condition,
                        Plugin.Framework,
                        Plugin.TextureProvider,
                        Plugin.GameInteropProvider,
                        Plugin.SigScanner,
                        Plugin.Log
                    )
            ),
            Register<HiddenPlayerMarkerSettings>(
                store,
                "hidden-player-markers",
                "Feature.Name.HiddenPlayerMarker",
                Enabled("EnableHiddenPlayerGroundMarker", true),
                (settings, save) =>
                    new HiddenPlayerMarkerFeature(
                        settings,
                        save,
                        reader,
                        Plugin.GameGui,
                        Plugin.PluginInterface,
                        Plugin.GameInteropProvider,
                        Plugin.SigScanner,
                        Plugin.Log
                    )
            ),
            Register<SceneSettings>(
                store,
                "scene",
                "Feature.Name.Scene",
                !safeMode
                    && (
                        store.LegacyEnabled("HideBgParts", false)
                        || store.LegacyEnabled("HideTerrain", false)
                        || store.LegacyEnabled("HideWater", false)
                        || store.LegacyEnabled("HideGrass", false)
                        || store.LegacyEnabled("HideAll3DScene", false)
                    ),
                (settings, save) => new SceneFeature(settings, save, Plugin.GameInteropProvider)
            ),
            Register<PreviewSettings>(
                store,
                "preview",
                "Feature.Name.Preview",
                !safeMode,
                (settings, save) => new PreviewFeature(settings, save, reader, commands, Plugin.GameGui, openSettings, registerCommand)
            ),
        ];
    }

    private static FeatureRegistration Register<T>(
        FeatureConfigStore store,
        string id,
        string titleKey,
        bool enabled,
        Func<T, Action, IFeature> create
    )
        where T : class, new() =>
        new(
            id,
            titleKey,
            enabled,
            () =>
            {
                var settings = store.Load<T>(id);
                store.Save(id, settings);
                return create(settings, () => store.Save(id, settings));
            }
        );
}
