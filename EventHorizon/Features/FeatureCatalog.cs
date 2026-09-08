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
    public static IEnumerable<IFeatureDefinition> Create(
        ICullingReader reader,
        ICullingCommands commands,
        Action openSettings,
        Action<FeatureScope, string, Action> registerCommand
    ) =>
        [
            DtrFeature.CreateDefinition(Plugin.DtrBar, reader, commands, openSettings),
            DtrBackgroundFeature.CreateDefinition(Plugin.AddonLifecycle, Plugin.GameGui, Plugin.Framework, Plugin.ClientState),
            TargetingMarkerFeature.CreateDefinition(
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
            ),
            HiddenPlayerMarkerFeature.CreateDefinition(
                reader,
                Plugin.GameGui,
                Plugin.PluginInterface,
                Plugin.GameInteropProvider,
                Plugin.SigScanner,
                Plugin.Log
            ),
            SceneFeature.CreateDefinition(Plugin.GameInteropProvider),
            PreviewFeature.CreateDefinition(reader, commands, Plugin.GameGui, openSettings, registerCommand),
        ];
}
