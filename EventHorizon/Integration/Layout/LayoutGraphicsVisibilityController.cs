using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;

namespace EventHorizon.Integration.Layout;

internal sealed unsafe class LayoutGraphicsVisibilityController : IDisposable
{
    private const int LayoutReadyState = 7;
    private const int TerrainPatchStatsLogIntervalMs = 1_000;
    private const string TerrainPatchQueueRenderCommandsSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 40 0F B7 79";

    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly Dictionary<nint, bool> hiddenBgPartGraphicsObjects = [];
    private readonly HashSet<nint> liveBgPartGraphicsObjects = [];
    private readonly List<nint> staleBgPartGraphicsObjects = [];

    private Hook<TerrainPatchQueueRenderCommandsDelegate>? terrainPatchQueueRenderCommandsHook;
    private bool hideTerrain;
    private bool currentAreaReady;
    private bool wasBgPartHidingEnabled;
    private nint lastActiveLayout;
    private uint lastTerritoryType;
    private bool lastAreaReady;
    private long terrainPatchFrameDetourCount;
    private long terrainPatchFrameSuppressedCount;
    private long terrainPatchTotalDetourCount;
    private long terrainPatchTotalSuppressedCount;
    private long terrainPatchMaxFrameDetourCount;
    private long terrainPatchMaxFrameSuppressedCount;
    private long terrainPatchStatsFrameCount;
    private long nextTerrainPatchStatsLog;

    private delegate void BgObjectVisitor(BgObject* graphicsObject);

    private delegate void TerrainPatchQueueRenderCommandsDelegate(void* patch, int contextId);

    public LayoutGraphicsVisibilityController(IGameInteropProvider gameInteropProvider, IClientState clientState, IPluginLog log)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.clientState = clientState;
        this.log = log;
        InitializeTerrainPatchQueueRenderCommandsHook();
    }

    public void Update(bool hideBgParts, bool hideTerrain)
    {
        this.hideTerrain = hideTerrain;
        var state = GetCurrentAreaState(clientState.TerritoryType);
        currentAreaReady = hideTerrain && state.IsReady;
        RecordTerrainPatchStatsFrame();
        ResetTerrainPatchStatsIfDisabled();

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
        hideTerrain = false;
        RestoreCurrentArea();
        terrainPatchQueueRenderCommandsHook?.Dispose();
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

    private void ResetTerrainPatchStatsIfDisabled()
    {
        if (hideTerrain)
        {
            return;
        }

        ResetTerrainPatchStats();
    }

    private void ResetTerrainPatchStats()
    {
        terrainPatchFrameDetourCount = 0;
        terrainPatchFrameSuppressedCount = 0;
        terrainPatchTotalDetourCount = 0;
        terrainPatchTotalSuppressedCount = 0;
        terrainPatchMaxFrameDetourCount = 0;
        terrainPatchMaxFrameSuppressedCount = 0;
        terrainPatchStatsFrameCount = 0;
        nextTerrainPatchStatsLog = 0;
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

    private void InitializeTerrainPatchQueueRenderCommandsHook()
    {
        try
        {
            terrainPatchQueueRenderCommandsHook = gameInteropProvider.HookFromSignature<TerrainPatchQueueRenderCommandsDelegate>(
                TerrainPatchQueueRenderCommandsSignature,
                TerrainPatchQueueRenderCommandsDetour
            );
            terrainPatchQueueRenderCommandsHook.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[LayoutGraphics] Failed to hook TerrainPatch::QueueRenderCommands.");
        }
    }

    private void TerrainPatchQueueRenderCommandsDetour(void* patch, int contextId)
    {
        terrainPatchFrameDetourCount++;
        if (hideTerrain && currentAreaReady)
        {
            terrainPatchFrameSuppressedCount++;
            return;
        }

        terrainPatchQueueRenderCommandsHook!.Original(patch, contextId);
    }

    private void RecordTerrainPatchStatsFrame()
    {
        if (!hideTerrain)
        {
            return;
        }

        terrainPatchStatsFrameCount++;
        terrainPatchTotalDetourCount += terrainPatchFrameDetourCount;
        terrainPatchTotalSuppressedCount += terrainPatchFrameSuppressedCount;
        terrainPatchMaxFrameDetourCount = Math.Max(terrainPatchMaxFrameDetourCount, terrainPatchFrameDetourCount);
        terrainPatchMaxFrameSuppressedCount = Math.Max(terrainPatchMaxFrameSuppressedCount, terrainPatchFrameSuppressedCount);
        terrainPatchFrameDetourCount = 0;
        terrainPatchFrameSuppressedCount = 0;

        var now = Environment.TickCount64;
        if (nextTerrainPatchStatsLog == 0)
        {
            nextTerrainPatchStatsLog = now + TerrainPatchStatsLogIntervalMs;
            return;
        }

        if (now < nextTerrainPatchStatsLog)
        {
            return;
        }

        nextTerrainPatchStatsLog = now + TerrainPatchStatsLogIntervalMs;
        log.Information(
            "[LayoutGraphics] TerrainPatch::QueueRenderCommands frames={FrameCount} calls={CallCount} suppressed={SuppressedCount} maxFrameCalls={MaxFrameCalls} maxFrameSuppressed={MaxFrameSuppressed} areaReady={AreaReady}",
            terrainPatchStatsFrameCount,
            terrainPatchTotalDetourCount,
            terrainPatchTotalSuppressedCount,
            terrainPatchMaxFrameDetourCount,
            terrainPatchMaxFrameSuppressedCount,
            currentAreaReady
        );
        terrainPatchTotalDetourCount = 0;
        terrainPatchTotalSuppressedCount = 0;
        terrainPatchMaxFrameDetourCount = 0;
        terrainPatchMaxFrameSuppressedCount = 0;
        terrainPatchStatsFrameCount = 0;
    }

    private readonly record struct LayoutAreaState(nint ActiveLayout, uint TerritoryType, bool IsReady);
}
