using System;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using EventHorizon.Interop.Vfx;
using EventHorizon.Preview;
using EventHorizon.Settings;
using EventHorizon.WorldGraphics;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class CullingController : IDisposable
{
    private const int RefreshIntervalMs = 200;
    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly PlayerCuller players;
    private readonly NonPlayerCuller nonPlayers;
    private readonly HiddenPlayerMarker hiddenPlayerMarker;
    private readonly PlayerPreview playerPreview;
    private readonly HiddenObjectTracker hiddenObjects = new();
    private readonly UpdateObjectArraysHook hook;
    private CullingRuntimeMode? currentMode;
    private long nextRefresh;

    public CullingController(
        IGameInteropProvider gameInteropProvider,
        Configuration configuration,
        IPlayerState playerState,
        ICondition condition,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IGameGui gameGui,
        StaticVfxController staticVfxController,
        IWorldDotOverlay worldDotOverlay,
        IPluginLog log
    )
    {
        this.configuration = configuration;
        this.playerState = playerState;
        this.condition = condition;
        players = new PlayerCuller(configuration, playerState, objectTable, targetManager, gameGui, log);
        nonPlayers = new NonPlayerCuller(configuration);
        hiddenPlayerMarker = new HiddenPlayerMarker(configuration, gameGui, staticVfxController, worldDotOverlay);
        playerPreview = new PlayerPreview(configuration);
        hook = new UpdateObjectArraysHook(gameInteropProvider, OnObjectArraysUpdated, log);
    }

    public int HiddenPlayerCount => hiddenObjects.HiddenPlayerCount;
    public PlayerPreviewSnapshot PlayerPreviewSnapshot => playerPreview.Snapshot;

    public CullingStatus GetStatus() => BuildStatus(GameObjectManager.Instance());

    public void Enable() => hook.Enable();

    public void Update()
    {
        var manager = GameObjectManager.Instance();
        var mode = UpdateRuntimeMode(manager, out var requiresRefresh);
        if (mode != CullingRuntimeMode.Active)
        {
            nonPlayers.Clear();
            hiddenPlayerMarker.Clear();
            playerPreview.Clear();
            return;
        }

        var topologyDirty = players.ConsumePlayerTopologyDirty();
        var now = Environment.TickCount64;
        if (requiresRefresh || topologyDirty || now >= nextRefresh)
        {
            nonPlayers.Refresh(manager);
            players.Update(manager, hiddenObjects, playerPreview);
            nextRefresh = Environment.TickCount64 + RefreshIntervalMs;
        }

        players.Tick(manager, hiddenObjects);
        nonPlayers.Tick(manager, hiddenObjects, HiddenObjectTracker.PluginHiddenFlags);
        hiddenObjects.PruneMissing(manager);
        hiddenPlayerMarker.Update(manager, hiddenObjects);
    }

    public void Refresh(bool resetRuleState = false)
    {
        if (resetRuleState)
        {
            players.ClearRuleState();
        }

        var manager = GameObjectManager.Instance();
        var mode = UpdateRuntimeMode(manager, out _);
        if (mode == CullingRuntimeMode.Active)
        {
            nonPlayers.Refresh(manager);
            players.Update(manager, hiddenObjects, playerPreview);
            nextRefresh = Environment.TickCount64 + RefreshIntervalMs;
            return;
        }

        nonPlayers.Clear();
        hiddenPlayerMarker.Clear();
        playerPreview.Clear();
    }

    public void RefreshPlayerPreview() => players.RefreshPlayerPreview(GameObjectManager.Instance(), playerPreview);

    public bool SetPreviewSelectedPlayer(uint? entityId) => playerPreview.SetSelectedPlayer(entityId);

    public void RecordChatMessage(IChatMessage message) => players.RecordChatMessage(message);

    public void Dispose()
    {
        hook.Dispose();
        RestoreAndClearAllState(GameObjectManager.Instance());
        nonPlayers.Clear();
        hiddenPlayerMarker.Clear();
        playerPreview.Clear();
    }

    private CullingRuntimeMode UpdateRuntimeMode(GameObjectManager* manager, out bool requiresRefresh)
    {
        var nextMode = DetermineRuntimeMode(manager);
        requiresRefresh = false;
        if (currentMode == nextMode)
        {
            return nextMode;
        }

        currentMode = nextMode;
        if (nextMode != CullingRuntimeMode.Active)
        {
            if (nextMode == CullingRuntimeMode.Disabled)
            {
                RestoreAndClearAllState(manager);
            }
            else
            {
                RestoreAndClearRuntimeState(manager);
            }
        }
        else
        {
            players.ClearRuntimeState();
            requiresRefresh = true;
        }

        return nextMode;
    }

    private CullingRuntimeMode DetermineRuntimeMode(GameObjectManager* manager)
    {
        if (!configuration.HideAllOtherPlayers)
        {
            return CullingRuntimeMode.Disabled;
        }

        if (!playerState.IsLoaded || manager == null)
        {
            return CullingRuntimeMode.PlayerUnavailable;
        }

        var status = BuildStatus(manager);
        if (status.SuspendedInDuty)
        {
            return CullingRuntimeMode.SuspendedDuty;
        }

        return status.SuspendedByLowPlayerCount ? CullingRuntimeMode.SuspendedLowPlayerCount : CullingRuntimeMode.Active;
    }

    private CullingStatus BuildStatus(GameObjectManager* manager)
    {
        var enabled = configuration.HideAllOtherPlayers;
        var playerAvailable = playerState.IsLoaded && manager != null;
        var otherPlayerCount = CountOtherPlayers(manager);
        return new(
            enabled,
            enabled
                && playerAvailable
                && configuration.DisableInDuty
                && (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56]),
            enabled
                && playerAvailable
                && configuration.DisableCullingBelowPlayerCount
                && otherPlayerCount < configuration.DisableCullingPlayerCountThreshold,
            otherPlayerCount
        );
    }

    private static int CountOtherPlayers(GameObjectManager* manager)
    {
        if (manager == null)
        {
            return 0;
        }

        var playerCount = 0;
        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject != null && gameObject->ObjectKind == ObjectKind.Pc)
            {
                playerCount++;
            }
        }

        return Math.Max(0, playerCount - 1);
    }

    private void RestoreAndClearAllState(GameObjectManager* manager)
    {
        RestoreHiddenObjects(manager);
        players.ClearAllState();
    }

    private void RestoreAndClearRuntimeState(GameObjectManager* manager)
    {
        RestoreHiddenObjects(manager);
        players.ClearRuntimeState();
    }

    private void RestoreHiddenObjects(GameObjectManager* manager)
    {
        if (manager != null)
        {
            hiddenObjects.RestoreAll(manager);
            return;
        }

        hiddenObjects.Clear();
    }

    private void OnObjectArraysUpdated(GameObjectManager* manager)
    {
        if (DetermineRuntimeMode(manager) != CullingRuntimeMode.Active)
        {
            players.ResetAdmissionGate();
            return;
        }

        players.ApplyAdmissionGate(manager, hiddenObjects);
    }
}

internal enum CullingRuntimeMode
{
    Disabled,
    PlayerUnavailable,
    SuspendedDuty,
    SuspendedLowPlayerCount,
    Active,
}

internal readonly record struct CullingStatus(bool Enabled, bool SuspendedInDuty, bool SuspendedByLowPlayerCount, int OtherPlayerCount);
