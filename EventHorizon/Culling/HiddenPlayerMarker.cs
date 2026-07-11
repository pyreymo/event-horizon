using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using EventHorizon.Interop.Vfx;
using EventHorizon.Settings;
using EventHorizon.WorldGraphics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class HiddenPlayerMarker(
    Configuration configuration,
    IGameGui gameGui,
    StaticVfxController staticVfxController,
    IWorldDotOverlay worldDotOverlay
)
{
    private const int MaxCreatesPerFrame = 8;
    private const string VfxPath = StaticVfxResourceRedirector.HiddenPlayerGroundMarkerPath;
    private readonly List<nint> hiddenPlayerAddresses = [];
    private readonly List<Candidate> candidates = [];
    private readonly HashSet<ulong> liveIds = [];
    private readonly List<WorldDot> dots = [];
    private bool vfxStyleActive;

    public void Update(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
        if (!configuration.EnableHiddenPlayerGroundMarker || manager == null)
        {
            Clear();
            return;
        }

        hiddenPlayerAddresses.Clear();
        candidates.Clear();
        liveIds.Clear();
        hiddenObjects.CollectHiddenPlayerAddresses(manager, hiddenPlayerAddresses);

        if (configuration.UseHiddenPlayerMarkerDot)
        {
            if (vfxStyleActive)
            {
                staticVfxController.ClearScope(StaticVfxScope.HiddenPlayerMarker);
                vfxStyleActive = false;
            }

            UpdateDots();
            hiddenPlayerAddresses.Clear();
            return;
        }

        vfxStyleActive = true;
        worldDotOverlay.Clear(WorldDotScope.HiddenPlayer);

        foreach (var address in hiddenPlayerAddresses)
        {
            var gameObject = (GameObject*)address;
            if (!TryGetPosition(gameObject, out var position))
            {
                continue;
            }

            var gameObjectId = (ulong)gameObject->GetGameObjectId();
            liveIds.Add(gameObjectId);
            var isActive = staticVfxController.IsActive(StaticVfxScope.HiddenPlayerMarker, gameObjectId, VfxPath);
            if (isActive || IsScreenVisible(position))
            {
                candidates.Add(new(gameObjectId, position, gameObject->Rotation, isActive));
            }
        }

        var createAttempts = 0;
        foreach (var candidate in candidates)
        {
            if (!candidate.IsActive && createAttempts >= MaxCreatesPerFrame)
            {
                continue;
            }

            if (!candidate.IsActive)
            {
                createAttempts++;
            }

            staticVfxController.ShowOrUpdate(
                StaticVfxScope.HiddenPlayerMarker,
                candidate.GameObjectId,
                VfxPath,
                candidate.Position,
                candidate.Rotation
            );
        }

        staticVfxController.PruneScopeExcept(StaticVfxScope.HiddenPlayerMarker, liveIds);
        hiddenPlayerAddresses.Clear();
        candidates.Clear();
        liveIds.Clear();
    }

    public void Clear()
    {
        staticVfxController.ClearScope(StaticVfxScope.HiddenPlayerMarker);
        worldDotOverlay.Clear(WorldDotScope.HiddenPlayer);
        vfxStyleActive = false;
        hiddenPlayerAddresses.Clear();
        candidates.Clear();
        liveIds.Clear();
        dots.Clear();
    }

    private static bool TryGetPosition(GameObject* gameObject, out Vector3 position)
    {
        position = default;
        if (gameObject == null || gameObject->VirtualTable == null)
        {
            return false;
        }

        var positionPtr = gameObject->GetPosition();
        if (positionPtr == null)
        {
            return false;
        }

        position = (Vector3)(*positionPtr);
        return true;
    }

    private bool IsScreenVisible(Vector3 position) => gameGui.WorldToScreen(position, out _, out var inView) && inView;

    private void UpdateDots()
    {
        dots.Clear();
        var color = WorldDot.PackColor(
            configuration.HiddenPlayerMarkerDotColorRed,
            configuration.HiddenPlayerMarkerDotColorGreen,
            configuration.HiddenPlayerMarkerDotColorBlue,
            configuration.HiddenPlayerMarkerDotColorAlpha
        );
        var radius = Math.Clamp(configuration.HiddenPlayerMarkerDotRadius, 1f, 20f);
        foreach (var address in hiddenPlayerAddresses)
        {
            if (TryGetPosition((GameObject*)address, out var position))
            {
                dots.Add(new(position, color, radius));
            }
        }

        worldDotOverlay.Replace(WorldDotScope.HiddenPlayer, CollectionsMarshal.AsSpan(dots));
    }

    private readonly record struct Candidate(ulong GameObjectId, Vector3 Position, float Rotation, bool IsActive);
}
