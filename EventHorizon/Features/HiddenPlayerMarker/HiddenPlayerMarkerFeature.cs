using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EventHorizon.Application;
using EventHorizon.Culling;
using EventHorizon.Interop.Vfx;
using EventHorizon.WorldGraphics;

namespace EventHorizon.Features.HiddenPlayerMarker;

internal sealed class HiddenPlayerMarkerFeature(
    HiddenPlayerMarkerSettings settings,
    Action save,
    ICullingReader reader,
    IGameGui gameGui,
    IDalamudPluginInterface pluginInterface,
    IGameInteropProvider interop,
    ISigScanner scanner,
    IPluginLog log
) : Feature<HiddenPlayerMarkerSettings>(settings, save)
{
    public override void Enable(FeatureScope scope)
    {
        var overlay = scope.Own(new WorldDotOverlay(gameGui));
        StaticVfxController? vfx = null;
        var liveIds = new HashSet<ulong>();
        var dots = new List<WorldDot>();
        scope.OnDraw(overlay.Draw);
        scope.OnUpdate(() =>
        {
            var snapshot = reader.Capture();
            if (snapshot.Status.Mode != CullingRuntimeMode.Active)
            {
                overlay.Clear(WorldDotScope.HiddenPlayer);
                vfx?.Clear();
                return;
            }
            if (Settings.UseHiddenPlayerMarkerDot)
            {
                vfx?.Clear();
                dots.Clear();
                var color = WorldDot.PackColor(
                    Settings.HiddenPlayerMarkerDotColorRed,
                    Settings.HiddenPlayerMarkerDotColorGreen,
                    Settings.HiddenPlayerMarkerDotColorBlue,
                    Settings.HiddenPlayerMarkerDotColorAlpha
                );
                foreach (var player in snapshot.Players)
                    if (player.Allowed == false)
                        dots.Add(new(player.Position, color, Settings.HiddenPlayerMarkerDotRadius));
                overlay.Replace(WorldDotScope.HiddenPlayer, CollectionsMarshal.AsSpan(dots));
                return;
            }
            overlay.Clear(WorldDotScope.HiddenPlayer);
            if (vfx == null)
            {
                scope.Own(new StaticVfxResourceRedirector(pluginInterface, interop, log));
                vfx = scope.Own(new StaticVfxController(interop, scanner, log));
            }
            liveIds.Clear();
            var creates = 0;
            foreach (var player in snapshot.Players)
            {
                if (player.Allowed != false)
                    continue;
                var id = player.Handle.GameObjectId;
                liveIds.Add(id);
                const string path = StaticVfxResourceRedirector.HiddenPlayerGroundMarkerPath;
                var active = vfx.IsActive(StaticVfxScope.HiddenPlayerMarker, id, path);
                if (!active && (creates >= 8 || !gameGui.WorldToScreen(player.Position, out _, out var inView) || !inView))
                    continue;
                if (!active)
                    creates++;
                vfx.ShowOrUpdate(StaticVfxScope.HiddenPlayerMarker, id, path, player.Position, player.Rotation);
            }
            vfx.PruneScopeExcept(StaticVfxScope.HiddenPlayerMarker, liveIds);
        });
    }

    public override void DrawSettings()
    {
        var changed = FeatureUi.Checkbox(
            "UseHiddenPlayerMarkerDot",
            Settings.UseHiddenPlayerMarkerDot,
            v => Settings.UseHiddenPlayerMarkerDot = v
        );
        if (Settings.UseHiddenPlayerMarkerDot)
        {
            changed |= FeatureUi.Color(
                "HiddenPlayerMarkerDotColor",
                Settings.HiddenPlayerMarkerDotColorRed,
                Settings.HiddenPlayerMarkerDotColorGreen,
                Settings.HiddenPlayerMarkerDotColorBlue,
                Settings.HiddenPlayerMarkerDotColorAlpha,
                (r, g, b, a) =>
                {
                    Settings.HiddenPlayerMarkerDotColorRed = r;
                    Settings.HiddenPlayerMarkerDotColorGreen = g;
                    Settings.HiddenPlayerMarkerDotColorBlue = b;
                    Settings.HiddenPlayerMarkerDotColorAlpha = a;
                }
            );
            changed |= FeatureUi.Slider(
                "HiddenPlayerMarkerDotSize",
                Settings.HiddenPlayerMarkerDotRadius,
                1,
                20,
                v => Settings.HiddenPlayerMarkerDotRadius = v
            );
        }
        if (changed)
            Save();
    }
}
