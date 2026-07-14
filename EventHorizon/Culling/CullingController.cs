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
    private const int RefreshIntervalMs = 100;
    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly PlayerCuller players;
    private readonly NonPlayerCuller nonPlayers;
    private readonly HiddenPlayerMarker hiddenPlayerMarker;
    private readonly PlayerPreview playerPreview;
    private readonly HiddenObjectTracker hiddenObjects = new();
    private readonly PlayerAdmissionGate admissionGate;
    private readonly UpdateObjectArraysHook updateObjectArraysHook;
    private readonly EnableDrawHook enableDrawHook;
    private CullingRuntimeMode? currentMode;
    private int otherPlayerCount;
    private long nextRefresh;

    public CullingController(
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
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
        admissionGate = new PlayerAdmissionGate();
        players = new PlayerCuller(configuration, playerState, objectTable, targetManager, gameGui, admissionGate, log);
        nonPlayers = new NonPlayerCuller(configuration);
        hiddenPlayerMarker = new HiddenPlayerMarker(configuration, gameGui, staticVfxController, worldDotOverlay);
        playerPreview = new PlayerPreview(configuration);
        updateObjectArraysHook = new UpdateObjectArraysHook(gameInteropProvider);
        try
        {
            enableDrawHook = new EnableDrawHook(gameInteropProvider, sigScanner, admissionGate);
        }
        catch
        {
            updateObjectArraysHook.Dispose();
            throw;
        }

        otherPlayerCount = CountOtherPlayers(GameObjectManager.Instance());
    }

    public int HiddenPlayerCount => hiddenObjects.HiddenPlayerCount;
    public PlayerPreviewSnapshot PlayerPreviewSnapshot => playerPreview.Snapshot;
    public bool TemporarilyShowAllPlayers { private get; set; }

    public CullingStatus GetStatus() => BuildStatus(GameObjectManager.Instance());

    public void Enable()
    {
        try
        {
            enableDrawHook.Enable();
            updateObjectArraysHook.Enable();
        }
        catch
        {
            enableDrawHook.Disable();
            updateObjectArraysHook.Disable();
            throw;
        }
    }

    public void Update()
    {
        admissionGate.BeginFrameworkFrame();
        var manager = GameObjectManager.Instance();
        var topologyChanged = updateObjectArraysHook.ConsumePlayerTopologyChanged();
        var admissionChanged = admissionGate.ConsumeChanged();
        var now = Environment.TickCount64;
        var refreshDue = now >= nextRefresh;
        if (topologyChanged)
        {
            admissionGate.PruneObservedPlayers(manager);
        }

        if (topologyChanged || refreshDue)
        {
            otherPlayerCount = CountOtherPlayers(manager);
        }

        var mode = UpdateRuntimeMode(manager, out var requiresRefresh);
        var shouldRefresh = requiresRefresh || topologyChanged || admissionChanged || refreshDue;
        if (mode != CullingRuntimeMode.Active)
        {
            if (topologyChanged || refreshDue)
            {
                nextRefresh = now + RefreshIntervalMs;
            }

            nonPlayers.Clear();
            hiddenPlayerMarker.Clear();
            playerPreview.Clear(GetPreviewEmptyReason(mode));
            return;
        }

        if (shouldRefresh)
        {
            hiddenObjects.PruneMissing(manager);
            nonPlayers.Refresh(manager);
            players.Update(manager, hiddenObjects, playerPreview);
            nextRefresh = now + RefreshIntervalMs;
        }

        players.Tick(manager, hiddenObjects);
        nonPlayers.Tick(manager, hiddenObjects, HiddenObjectTracker.PluginHiddenFlags);
        hiddenPlayerMarker.Update(manager, hiddenObjects);
    }

    public void Refresh(bool resetRuleState = false)
    {
        if (resetRuleState)
        {
            players.ClearRuleState();
        }

        var manager = GameObjectManager.Instance();
        var topologyChanged = updateObjectArraysHook.ConsumePlayerTopologyChanged();
        if (topologyChanged)
        {
            admissionGate.PruneObservedPlayers(manager);
        }

        otherPlayerCount = CountOtherPlayers(manager);
        hiddenObjects.PruneMissing(manager);
        var now = Environment.TickCount64;
        var mode = UpdateRuntimeMode(manager, out _);
        nextRefresh = now + RefreshIntervalMs;
        if (mode == CullingRuntimeMode.Active)
        {
            nonPlayers.Refresh(manager);
            players.Update(manager, hiddenObjects, playerPreview);
            return;
        }

        nonPlayers.Clear();
        hiddenPlayerMarker.Clear();
        playerPreview.Clear(GetPreviewEmptyReason(mode));
    }

    public void RefreshPlayerPreview() => players.RefreshPlayerPreview(GameObjectManager.Instance(), playerPreview);

    public bool SetPreviewSelectedPlayer(uint? entityId) => playerPreview.SetSelectedPlayer(entityId);

    public void RecordChatMessage(IChatMessage message) => players.RecordChatMessage(message);

    public void Dispose()
    {
        enableDrawHook.Disable();
        admissionGate.Stop(GameObjectManager.Instance());
        RestoreAndClearAllState(GameObjectManager.Instance());
        nonPlayers.Clear();
        hiddenPlayerMarker.Clear();
        playerPreview.Clear(PlayerPreviewEmptyReason.PlayerUnavailable);
        updateObjectArraysHook.Disable();
        enableDrawHook.Dispose();
        updateObjectArraysHook.Dispose();
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
            admissionGate.Stop(manager);
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
            admissionGate.Activate(manager);
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

        if (TemporarilyShowAllPlayers)
        {
            return CullingRuntimeMode.SuspendedTemporaryReveal;
        }

        if (!playerState.IsLoaded || manager == null)
        {
            return CullingRuntimeMode.PlayerUnavailable;
        }

        if (configuration.DisableInDuty && (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56]))
        {
            return CullingRuntimeMode.SuspendedDuty;
        }

        if (configuration.DisableCullingBelowPlayerCount && otherPlayerCount < configuration.DisableCullingPlayerCountThreshold)
        {
            return CullingRuntimeMode.SuspendedLowPlayerCount;
        }

        return CullingRuntimeMode.Active;
    }

    private static PlayerPreviewEmptyReason GetPreviewEmptyReason(CullingRuntimeMode mode) =>
        mode switch
        {
            CullingRuntimeMode.Disabled => PlayerPreviewEmptyReason.PlayerHidingDisabled,
            CullingRuntimeMode.SuspendedTemporaryReveal => PlayerPreviewEmptyReason.TemporaryReveal,
            CullingRuntimeMode.PlayerUnavailable => PlayerPreviewEmptyReason.PlayerUnavailable,
            CullingRuntimeMode.SuspendedDuty => PlayerPreviewEmptyReason.SuspendedInDuty,
            CullingRuntimeMode.SuspendedLowPlayerCount => PlayerPreviewEmptyReason.SuspendedByLowPlayerCount,
            _ => PlayerPreviewEmptyReason.NoOtherPlayers,
        };

    private CullingStatus BuildStatus(GameObjectManager* manager)
    {
        var enabled = configuration.HideAllOtherPlayers;
        var playerAvailable = playerState.IsLoaded && manager != null;
        var currentOtherPlayerCount = manager == null ? 0 : otherPlayerCount;
        return new(
            enabled,
            enabled && TemporarilyShowAllPlayers,
            enabled
                && playerAvailable
                && configuration.DisableInDuty
                && (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56]),
            enabled
                && playerAvailable
                && configuration.DisableCullingBelowPlayerCount
                && currentOtherPlayerCount < configuration.DisableCullingPlayerCountThreshold,
            currentOtherPlayerCount
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
}

internal enum CullingRuntimeMode
{
    Disabled,
    SuspendedTemporaryReveal,
    PlayerUnavailable,
    SuspendedDuty,
    SuspendedLowPlayerCount,
    Active,
}

internal readonly record struct CullingStatus(
    bool Enabled,
    bool SuspendedByTemporaryReveal,
    bool SuspendedInDuty,
    bool SuspendedByLowPlayerCount,
    int OtherPlayerCount
);
