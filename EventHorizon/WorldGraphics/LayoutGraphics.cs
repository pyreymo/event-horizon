using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using EventHorizon.Interop;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;

namespace EventHorizon.WorldGraphics;

internal sealed unsafe class LayoutGraphics : IDisposable
{
    private static readonly TimeSpan CallbackDrainTimeout = TimeSpan.FromSeconds(2);
    private const int LayoutReadyState = 7;
    private const string TerrainPatchQueueRenderCommandsSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 40 0F B7 79";

    private readonly IClientState clientState;
    private readonly HookCallbackTracker terrainCallbackTracker;
    private readonly Hook<TerrainPatchQueueRenderCommandsDelegate>? terrainPatchQueueRenderCommandsHook;
    private TerrainPatchQueueRenderCommandsDelegate? terrainPatchQueueRenderCommandsOriginal;
    private readonly Dictionary<nint, bool> hiddenBgPartGraphicsObjects = [];
    private readonly HashSet<nint> liveBgPartGraphicsObjects = [];
    private readonly List<nint> staleBgPartGraphicsObjects = [];

    private int hideTerrain;
    private int currentAreaReady;
    private int disposed;
    private bool wasBgPartHidingEnabled;
    private nint lastActiveLayout;
    private uint lastTerritoryType;
    private bool lastAreaReady;

    private delegate void BgObjectVisitor(BgObject* graphicsObject);

    private delegate void TerrainPatchQueueRenderCommandsDelegate(void* patch, int contextId);

    public LayoutGraphics(IGameInteropProvider gameInteropProvider, IClientState clientState, IPluginLog log)
    {
        this.clientState = clientState;
        terrainCallbackTracker = new HookCallbackTracker("TerrainPatch.QueueRenderCommands", log);

        Hook<TerrainPatchQueueRenderCommandsDelegate>? createdHook = null;
        try
        {
            createdHook = gameInteropProvider.HookFromSignature<TerrainPatchQueueRenderCommandsDelegate>(
                TerrainPatchQueueRenderCommandsSignature,
                TerrainPatchQueueRenderCommandsDetour
            );
            Volatile.Write(ref terrainPatchQueueRenderCommandsOriginal, createdHook.Original);
            terrainPatchQueueRenderCommandsHook = createdHook;
            terrainCallbackTracker.MarkReady();
            createdHook.Enable();
        }
        catch (Exception ex)
        {
            terrainCallbackTracker.ReportException(ex, "initialize TerrainPatch.QueueRenderCommands hook");
            terrainCallbackTracker.BeginStop();
            try
            {
                createdHook?.Dispose();
            }
            catch (Exception disposeException)
            {
                terrainCallbackTracker.ReportException(disposeException, "dispose partially initialized terrain hook");
            }

            terrainCallbackTracker.MarkStopped();
        }
    }

    public void Update(bool hideBgParts, bool hideTerrain)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref this.hideTerrain, hideTerrain ? 1 : 0);
        var state = GetCurrentAreaState(clientState.TerritoryType);
        Volatile.Write(ref currentAreaReady, hideTerrain && state.IsReady ? 1 : 0);

        if (!hideBgParts)
        {
            if (wasBgPartHidingEnabled || hiddenBgPartGraphicsObjects.Count > 0)
            {
                RestoreCurrentArea();
            }

            wasBgPartHidingEnabled = false;
            RememberState(state);
            return;
        }

        var shouldApply =
            !wasBgPartHidingEnabled
            || lastActiveLayout != state.ActiveLayout
            || lastTerritoryType != state.TerritoryType
            || (!lastAreaReady && state.IsReady);

        wasBgPartHidingEnabled = true;
        RememberState(state);
        if (!state.IsReady)
        {
            return;
        }

        if (shouldApply)
        {
            HideCurrentArea();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref hideTerrain, 0);
        Volatile.Write(ref currentAreaReady, 0);

        terrainCallbackTracker.BeginStop();
        try
        {
            terrainPatchQueueRenderCommandsHook?.Disable();
        }
        catch (Exception ex)
        {
            terrainCallbackTracker.ReportException(ex, "disable TerrainPatch.QueueRenderCommands hook");
        }

        terrainCallbackTracker.WaitForDrain(CallbackDrainTimeout);
        RestoreCurrentArea();

        try
        {
            terrainPatchQueueRenderCommandsHook?.Dispose();
        }
        catch (Exception ex)
        {
            terrainCallbackTracker.ReportException(ex, "dispose TerrainPatch.QueueRenderCommands hook");
        }
        finally
        {
            terrainCallbackTracker.MarkStopped();
        }
    }

    private void HideCurrentArea()
    {
        liveBgPartGraphicsObjects.Clear();

        VisitCurrentAreaBgPartGraphicsObjects(graphicsObject =>
        {
            var address = (nint)graphicsObject;

            liveBgPartGraphicsObjects.Add(address);
            hiddenBgPartGraphicsObjects.TryAdd(address, graphicsObject->IsVisible);

            graphicsObject->IsVisible = false;
        });

        staleBgPartGraphicsObjects.Clear();

        foreach (var address in hiddenBgPartGraphicsObjects.Keys)
        {
            if (!liveBgPartGraphicsObjects.Contains(address))
            {
                staleBgPartGraphicsObjects.Add(address);
            }
        }

        foreach (var address in staleBgPartGraphicsObjects)
        {
            hiddenBgPartGraphicsObjects.Remove(address);
        }

        staleBgPartGraphicsObjects.Clear();
        liveBgPartGraphicsObjects.Clear();
    }

    private void RestoreCurrentArea()
    {
        VisitCurrentAreaBgPartGraphicsObjects(graphicsObject =>
        {
            if (hiddenBgPartGraphicsObjects.TryGetValue((nint)graphicsObject, out var wasVisible))
            {
                graphicsObject->IsVisible = wasVisible;
            }
        });

        hiddenBgPartGraphicsObjects.Clear();
    }

    private void RememberState(LayoutAreaState state)
    {
        lastActiveLayout = state.ActiveLayout;
        lastTerritoryType = state.TerritoryType;
        lastAreaReady = state.IsReady;
    }

    private static void VisitCurrentAreaBgPartGraphicsObjects(BgObjectVisitor visitor)
    {
        var layoutWorld = LayoutWorld.Instance();
        if (layoutWorld == null)
        {
            return;
        }

        VisitLayoutBgParts(layoutWorld->GlobalLayout, visitor);
        VisitLayoutBgParts(layoutWorld->ActiveLayout, visitor);
    }

    private static LayoutAreaState GetCurrentAreaState(uint territoryType)
    {
        var layoutWorld = LayoutWorld.Instance();
        var activeLayout = layoutWorld == null ? null : layoutWorld->ActiveLayout;
        return new LayoutAreaState((nint)activeLayout, territoryType, activeLayout != null && activeLayout->InitState >= LayoutReadyState);
    }

    private static void VisitLayoutBgParts(LayoutManager* layout, BgObjectVisitor visitor)
    {
        if (layout == null)
        {
            return;
        }

        if (
            !layout->InstancesByType.TryGetValue(InstanceType.BgPart, out var bgPartInstances, copyCtor: false)
            || bgPartInstances.Value == null
        )
        {
            return;
        }

        foreach (var instancePointer in bgPartInstances.Value->Values)
        {
            var layoutInstance = instancePointer.Value;
            if (layoutInstance == null)
            {
                continue;
            }

            var bgPart = (BgPartsLayoutInstance*)layoutInstance;
            if (bgPart->GraphicsObject != null)
            {
                visitor(bgPart->GraphicsObject);
            }
        }
    }

    private void TerrainPatchQueueRenderCommandsDetour(void* patch, int contextId)
    {
        using var callback = terrainCallbackTracker.Enter();

        var callOriginal = Volatile.Read(ref terrainPatchQueueRenderCommandsOriginal);
        if (callOriginal is null)
        {
            // Dropping one render submission is safer than touching an unpublished hook trampoline.
            terrainCallbackTracker.ReportMissingOriginal();
            return;
        }

        if (
            terrainCallbackTracker.ShouldRunPluginLogic
            && Volatile.Read(ref disposed) == 0
            && Volatile.Read(ref hideTerrain) != 0
            && Volatile.Read(ref currentAreaReady) != 0
        )
        {
            return;
        }

        callOriginal(patch, contextId);
    }

    private readonly record struct LayoutAreaState(nint ActiveLayout, uint TerritoryType, bool IsReady);
}
