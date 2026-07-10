using System;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using EventHorizon.Interop.Vfx;
using EventHorizon.Preview;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class CullingController : IDisposable
{
    private const int RefreshIntervalMs = 200;
    private const VisibilityFlags HiddenFlags = (VisibilityFlags)0x1000 | VisibilityFlags.Nameplate | VisibilityFlags.Model;
    private readonly PlayerCuller players;
    private readonly NonPlayerCuller nonPlayers;
    private readonly HiddenPlayerMarker hiddenPlayerMarker;
    private readonly PlayerPreview playerPreview;
    private readonly HiddenObjectTracker hiddenObjects = new();
    private readonly UpdateObjectArraysHook hook;
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
        IPluginLog log
    )
    {
        players = new PlayerCuller(configuration, playerState, condition, objectTable, targetManager, gameGui, log);
        nonPlayers = new NonPlayerCuller(configuration);
        hiddenPlayerMarker = new HiddenPlayerMarker(configuration, gameGui, staticVfxController);
        playerPreview = new PlayerPreview(configuration);
        hook = new UpdateObjectArraysHook(gameInteropProvider, manager => players.ApplyPlayerAdmissionGate(manager, hiddenObjects));
    }

    public int HiddenPlayerCount => hiddenObjects.HiddenPlayerCount;
    public PlayerKeepBudgetStats KeepBudgetStats => players.GetKeepBudgetStats();
    public PlayerPreviewSnapshot PlayerPreviewSnapshot => playerPreview.Snapshot;

    public void Enable() => hook.Enable();

    public void Update()
    {
        var manager = GameObjectManager.Instance();
        var runtime = players.SynchronizeRuntimeMode(manager, hiddenObjects);
        var topologyDirty = players.ConsumePlayerTopologyDirty();
        var now = Environment.TickCount64;
        var schedule = CullingFrameSchedule.Decide(runtime.Mode, runtime.RequiresRefresh || now >= nextRefresh, topologyDirty);

        if (!schedule.Tick)
        {
            nonPlayers.Clear();
            hiddenPlayerMarker.Clear();
            playerPreview.Clear();
            return;
        }

        if (schedule.Refresh)
        {
            nonPlayers.Refresh(manager);
            players.Update(manager, hiddenObjects, playerPreview, refreshPlayerPreview: false);
            nextRefresh = Environment.TickCount64 + RefreshIntervalMs;
        }

        players.Tick(manager, hiddenObjects);
        nonPlayers.Tick(manager, hiddenObjects, HiddenFlags);
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
        var runtime = players.SynchronizeRuntimeMode(manager, hiddenObjects);
        if (runtime.Mode == CullingRuntimeMode.Active)
        {
            nonPlayers.Refresh(manager);
            players.Update(manager, hiddenObjects, playerPreview, refreshPlayerPreview: false);
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
        players.Reset(GameObjectManager.Instance(), hiddenObjects);
        nonPlayers.Clear();
        hiddenPlayerMarker.Clear();
        playerPreview.Clear();
    }
}
