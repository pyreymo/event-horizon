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
    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly DrawAdmissionPolicy policy;
    private readonly NativeDrawCandidateHook candidatesHook;
    private readonly HiddenPlayerMarker hiddenPlayerMarker;
    private readonly PlayerPreview playerPreview;
    private CullingRuntimeMode? currentMode;

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
        WorldDotOverlay worldDotOverlay,
        IPluginLog log
    )
    {
        this.configuration = configuration;
        this.playerState = playerState;
        this.condition = condition;
        policy = new(configuration, objectTable, targetManager, gameGui);
        hiddenPlayerMarker = new(configuration, gameGui, staticVfxController, worldDotOverlay);
        playerPreview = new(configuration);
        candidatesHook = new(gameInteropProvider, sigScanner, ApplyPolicy, log);
    }

    public int HiddenPlayerCount
    {
        get
        {
            var manager = GameObjectManager.Instance();
            if (manager == null || currentMode != CullingRuntimeMode.Active || candidatesHook.Failed)
                return 0;
            var count = 0;
            foreach (var target in policy.Decisions)
                if (!target.Allowed && target.Resolve(manager) != null)
                    count++;
            return count;
        }
    }

    public PlayerPreviewSnapshot PlayerPreviewSnapshot => playerPreview.Snapshot;
    public bool TemporarilyShowAllPlayers { private get; set; }

    public void Enable() => candidatesHook.Enable();

    public bool SetPreviewSelectedPlayer(uint? entityId) => playerPreview.SetSelectedPlayer(entityId);

    public void RecordChatMessage(IChatMessage message) => policy.RecordChatMessage(message);

    private void ApplyPolicy(Span<NativeDrawCandidate> candidates)
    {
        var manager = GameObjectManager.Instance();
        if (UpdateMode(manager) == CullingRuntimeMode.Active)
            policy.Apply(candidates, manager, playerPreview.ActiveSelectedPlayerEntityId);
    }

    public void Update()
    {
        var manager = GameObjectManager.Instance();
        var mode = UpdateMode(manager);
        if (mode == CullingRuntimeMode.Active)
            hiddenPlayerMarker.Update(manager, policy.Decisions);
        else
        {
            hiddenPlayerMarker.Clear();
            playerPreview.Clear(GetPreviewEmptyReason(mode));
        }
    }

    public void Refresh(bool resetRuleState = false)
    {
        if (resetRuleState)
            policy.ClearRules();
        Update();
        // The next native pass evaluates configuration changes and new arrivals.
    }

    public void RefreshPlayerPreview()
    {
        var manager = GameObjectManager.Instance();
        if (UpdateMode(manager) == CullingRuntimeMode.Active)
            playerPreview.Refresh(manager, policy.Decisions);
    }

    public void Dispose()
    {
        candidatesHook.Dispose();
        policy.Clear();
        policy.ClearRules();
        hiddenPlayerMarker.Clear();
        playerPreview.Clear(PlayerPreviewEmptyReason.PlayerUnavailable);
        // No restoration pass: the game rebuilds candidates on its next Update.
    }

    private CullingRuntimeMode UpdateMode(GameObjectManager* manager)
    {
        var mode = DetermineRuntimeMode(manager);
        if (currentMode != mode)
        {
            policy.Clear();
            if (mode is CullingRuntimeMode.Disabled or CullingRuntimeMode.PlayerUnavailable)
                policy.ClearRules();
            currentMode = mode;
        }
        return mode;
    }

    private CullingRuntimeMode DetermineRuntimeMode(GameObjectManager* manager)
    {
        if (!configuration.HideAllOtherPlayers)
            return CullingRuntimeMode.Disabled;
        if (candidatesHook.Failed)
            return CullingRuntimeMode.NativeHookFailed;
        if (TemporarilyShowAllPlayers)
            return CullingRuntimeMode.SuspendedTemporaryReveal;
        if (!playerState.IsLoaded || manager == null)
            return CullingRuntimeMode.PlayerUnavailable;
        if (configuration.DisableInDuty && (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56]))
            return CullingRuntimeMode.SuspendedDuty;
        if (configuration.DisableCullingBelowPlayerCount && CountOtherPlayers(manager) < configuration.DisableCullingPlayerCountThreshold)
            return CullingRuntimeMode.SuspendedLowPlayerCount;
        return CullingRuntimeMode.Active;
    }

    public CullingStatus GetStatus()
    {
        var manager = GameObjectManager.Instance();
        var count = CountOtherPlayers(manager);
        var enabled = configuration.HideAllOtherPlayers && !candidatesHook.Failed;
        var available = playerState.IsLoaded && manager != null;
        return new(
            enabled,
            enabled && TemporarilyShowAllPlayers,
            enabled
                && available
                && configuration.DisableInDuty
                && (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56]),
            enabled
                && available
                && configuration.DisableCullingBelowPlayerCount
                && count < configuration.DisableCullingPlayerCountThreshold,
            count
        );
    }

    private static int CountOtherPlayers(GameObjectManager* manager)
    {
        if (manager == null)
            return 0;
        var count = 0;
        for (var index = CharacterObjectSlots.FirstRemoteSlot; index <= CharacterObjectSlots.LastEvenSlot; index += 2)
        {
            var obj = manager->Objects.IndexSorted[index].Value;
            if (obj != null && obj->ObjectKind == ObjectKind.Pc)
                count++;
        }
        return count;
    }

    private static PlayerPreviewEmptyReason GetPreviewEmptyReason(CullingRuntimeMode mode) =>
        mode switch
        {
            CullingRuntimeMode.Disabled => PlayerPreviewEmptyReason.PlayerHidingDisabled,
            CullingRuntimeMode.SuspendedTemporaryReveal => PlayerPreviewEmptyReason.TemporaryReveal,
            CullingRuntimeMode.SuspendedDuty => PlayerPreviewEmptyReason.SuspendedInDuty,
            CullingRuntimeMode.SuspendedLowPlayerCount => PlayerPreviewEmptyReason.SuspendedByLowPlayerCount,
            CullingRuntimeMode.NativeHookFailed => PlayerPreviewEmptyReason.NativeHookFailed,
            _ => PlayerPreviewEmptyReason.PlayerUnavailable,
        };
}

internal enum CullingRuntimeMode
{
    Disabled,
    SuspendedTemporaryReveal,
    PlayerUnavailable,
    SuspendedDuty,
    SuspendedLowPlayerCount,
    NativeHookFailed,
    Active,
}

internal readonly record struct CullingStatus(
    bool Enabled,
    bool SuspendedByTemporaryReveal,
    bool SuspendedInDuty,
    bool SuspendedByLowPlayerCount,
    int OtherPlayerCount
);
