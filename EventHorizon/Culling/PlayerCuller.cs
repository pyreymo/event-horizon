using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using EventHorizon.Preview;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class PlayerCuller(
    Configuration configuration,
    IPlayerState playerState,
    ICondition condition,
    IObjectTable objectTable,
    ITargetManager targetManager,
    IGameGui gameGui,
    IPluginLog log
)
{
    private const int MaxPlayerRelatedObjectIndex = 199;
    private const VisibilityFlags PluginCustomProbe = (VisibilityFlags)0x1000;
    private const VisibilityFlags InvisibleFlag = PluginCustomProbe | VisibilityFlags.Nameplate | VisibilityFlags.Model;

    private readonly Configuration configuration = configuration;
    private readonly IPlayerState playerState = playerState;
    private readonly ICondition condition = condition;
    private readonly IObjectTable objectTable = objectTable;
    private readonly IGameGui gameGui = gameGui;
    private readonly PlayerKeepRules playerKeepRules = new(configuration, objectTable, targetManager);
    private readonly PlayerKeepPlan playerKeepPlan = new();
    private readonly List<PlayerKeepCandidate> playerKeepCandidates = [];
    private readonly PlayerVisibilityPlanner playerVisibilityPlanner = new(exception =>
        log.Error(exception, "Player visibility selection failed; using legacy fallback.")
    );
    private readonly ShowTransitionBudget showTransitionBudget = new();
    private readonly PlayerAdmissionGate playerAdmissionGate = new();
    private readonly PlayerTopologyDirtySignal playerTopologyDirtySignal = new();
    private readonly PlayerVisibilityAppliedState appliedVisibilityState = new();
    private readonly PlayerObjectIdentity?[] playerAdmissionSlotIdentities = new PlayerObjectIdentity?[
        PlayerAdmissionGate.LastPlayerSlot + 1
    ];
    private PlayerKeepBudgetStats keepBudgetStats;
    private int nextPlayerVisibilityGeneration;
    private PlayerVisibilityReconciliation? latestPlayerVisibilityReconciliation;
    private readonly CullingRuntimeModeTransition runtimeModeTransition = new();

    #region Lifecycle

    public CullingRuntimeSynchronization SynchronizeRuntimeMode(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
        var nextMode = DetermineRuntimeMode(manager);
        var transition = runtimeModeTransition.Synchronize(nextMode);
        if (!transition.Changed)
        {
            return new(nextMode, RequiresRefresh: false);
        }

        if (transition.EnterInactive)
        {
            EnterInactiveMode(manager, hiddenObjects, transition.ClearLongTermRules);
        }
        else if (transition.RebuildActive)
        {
            ClearPublishedPlayerVisibilityState();
        }

        return new(nextMode, RequiresRefresh: transition.RebuildActive);
    }

    public void Update(GameObjectManager* manager, HiddenObjectTracker hiddenObjects, PlayerPreview preview, bool refreshPlayerPreview)
    {
        if (manager == null)
        {
            Clear(hiddenObjects);
            return;
        }

        if (!IsCullingEnabled())
        {
            Reset(manager, hiddenObjects);
            return;
        }

        if (ShouldSuspendCullingInDuty())
        {
            playerVisibilityPlanner.Reset();
            RestoreHiddenObjects(manager, hiddenObjects);
            return;
        }

        if (ShouldSuspendCulling(manager))
        {
            playerVisibilityPlanner.Reset();
            RestoreHiddenObjects(manager, hiddenObjects);
            return;
        }

        playerKeepRules.BeforeUpdate();
        if (!playerState.IsLoaded)
        {
            playerVisibilityPlanner.Reset();
            RestoreHiddenObjects(manager, hiddenObjects);
            return;
        }

        playerKeepPlan.Update(configuration, GetPlayerKeepCandidates(manager));
        var playerVisibilityPlan = playerVisibilityPlanner.BuildPlan(
            ++nextPlayerVisibilityGeneration,
            manager,
            playerKeepPlan,
            preview.ActiveSelectedPlayerEntityId
        );
        var legacyTargetSet = playerVisibilityPlanner.BuildLegacyTarget(playerVisibilityPlan);
        var frameState = playerVisibilityPlanner.BuildFrame(
            playerVisibilityPlan,
            legacyTargetSet,
            configuration.LimitVisiblePlayerCount,
            configuration.VisiblePlayerCountLimit,
            objectTable.LocalPlayer?.Position,
            hiddenObjects
        );
        keepBudgetStats = frameState.BudgetStats;
        appliedVisibilityState.Publish(frameState);
        latestPlayerVisibilityReconciliation = frameState.Reconciliation;
        playerVisibilityPlanner.Commit(frameState);
        if (refreshPlayerPreview)
        {
            preview.Refresh(manager, frameState.ActiveTarget);
        }
    }

    public void Tick(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
        if (manager == null)
        {
            Clear(hiddenObjects);
            return;
        }

        if (runtimeModeTransition.Current != CullingRuntimeMode.Active)
        {
            Reset(manager, hiddenObjects);
            return;
        }

        if (latestPlayerVisibilityReconciliation == null)
        {
            return;
        }

        TickVisibility(manager, hiddenObjects);
    }

    public void ApplyPlayerAdmissionGate(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
        if (
            manager == null
            || !IsCullingEnabled()
            || !playerState.IsLoaded
            || ShouldSuspendCullingInDuty()
            || ShouldSuspendCulling(manager)
        )
        {
            playerAdmissionGate.ResetTracking();
            playerTopologyDirtySignal.Clear();
            return;
        }

        for (
            var slot = PlayerAdmissionGate.FirstPlayerSlot;
            slot <= PlayerAdmissionGate.LastPlayerSlot;
            slot += PlayerAdmissionGate.PlayerSlotStep
        )
        {
            var gameObject = manager->Objects.IndexSorted[slot].Value;
            playerAdmissionSlotIdentities[slot] =
                gameObject != null && gameObject->ObjectKind == ObjectKind.Pc ? PlayerObjectIdentity.From(gameObject) : null;
        }

        var result = playerAdmissionGate.Apply(
            playerAdmissionSlotIdentities,
            appliedVisibilityState,
            change =>
            {
                var gameObject = manager->Objects.IndexSorted[change.Slot].Value;
                if (!change.CurrentIdentity.HasValue || !change.CurrentIdentity.Value.Matches(gameObject))
                {
                    throw new InvalidOperationException("Admission slot identity changed before the hard hide could be applied.");
                }

                Hide(gameObject, change.Slot, hiddenObjects);
            }
        );
        playerTopologyDirtySignal.MarkFrom(result);
    }

    public void Reset(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
        if (manager == null)
        {
            Clear(hiddenObjects);
            return;
        }

        RestoreHiddenObjects(manager, hiddenObjects);
        Clear(hiddenObjects);
    }

    private void RestoreHiddenObjects(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
        hiddenObjects.RestoreAll(manager);
    }

    public void ClearRuleState()
    {
        playerKeepRules.Clear();
        playerVisibilityPlanner.Reset();
    }

    public void RecordChatMessage(IChatMessage message)
    {
        playerKeepRules.RecordChatMessage(message);
    }

    public void RefreshPlayerPreview(GameObjectManager* manager, PlayerPreview preview)
    {
        if (manager == null || !IsCullingEnabled() || !playerState.IsLoaded || appliedVisibilityState.ActiveTarget == null)
        {
            return;
        }

        preview.Refresh(manager, appliedVisibilityState.ActiveTarget);
    }

    #endregion

    #region Visibility

    private static void Hide(GameObject* gameObject, int objectIndex, HiddenObjectTracker hiddenObjects)
    {
        hiddenObjects.Hide(gameObject, InvisibleFlag, objectIndex);
    }

    private static void RestoreIfHidden(GameObject* gameObject, HiddenObjectTracker hiddenObjects)
    {
        hiddenObjects.RestoreIfHidden(gameObject);
    }

    private void ApplyPlayerVisibilityReconciliation(
        GameObjectManager* manager,
        PlayerVisibilityReconciliation reconciliation,
        HiddenObjectTracker hiddenObjects
    )
    {
        foreach (var action in reconciliation.Actions)
        {
            ApplyPlayerVisibilityAction(manager, action, hiddenObjects);
        }
    }

    private void ApplyPlayerVisibilityAction(GameObjectManager* manager, PlayerVisibilityAction action, HiddenObjectTracker hiddenObjects)
    {
        switch (action.Kind)
        {
            case PlayerVisibilityActionKind.Show:
                ApplyShowAction(manager, action.Target, hiddenObjects);
                break;
            case PlayerVisibilityActionKind.Hide:
                ApplyHideAction(manager, action.Target, hiddenObjects);
                break;
            case PlayerVisibilityActionKind.Swap:
                ApplySwapAction(manager, action, hiddenObjects);
                break;
        }
    }

    private void TickVisibility(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
        if (latestPlayerVisibilityReconciliation == null)
        {
            return;
        }

        showTransitionBudget.BeginFrame();
        ApplyPlayerVisibilityReconciliation(manager, latestPlayerVisibilityReconciliation, hiddenObjects);
    }

    private void ApplyShowAction(GameObjectManager* manager, PlayerVisibilityTarget target, HiddenObjectTracker hiddenObjects)
    {
        var gameObject = FindPlayerObject(manager, target.Identity, target.ObjectIndex);
        if (gameObject == null)
        {
            return;
        }

        var wasHidden = hiddenObjects.IsHidden(gameObject);
        if (wasHidden && !showTransitionBudget.CanStartShow())
        {
            return;
        }

        RestoreIfHidden(gameObject, hiddenObjects);
        if (wasHidden && !hiddenObjects.IsHidden(gameObject))
        {
            showTransitionBudget.ConsumeShow();
        }
    }

    private void ApplyHideAction(GameObjectManager* manager, PlayerVisibilityTarget target, HiddenObjectTracker hiddenObjects)
    {
        var gameObject = FindPlayerObject(manager, target.Identity, target.ObjectIndex);
        if (gameObject != null)
        {
            Hide(gameObject, target.ObjectIndex, hiddenObjects);
        }
    }

    private void ApplySwapAction(GameObjectManager* manager, PlayerVisibilityAction action, HiddenObjectTracker hiddenObjects)
    {
        if (!action.PairedTarget.HasValue)
        {
            ApplyShowAction(manager, action.Target, hiddenObjects);
            return;
        }

        var incoming = FindPlayerObject(manager, action.Target.Identity, action.Target.ObjectIndex);
        if (incoming == null)
        {
            return;
        }

        var incomingWasHidden = hiddenObjects.IsHidden(incoming);
        if (incomingWasHidden && !showTransitionBudget.CanStartShow())
        {
            return;
        }

        var outgoingTarget = action.PairedTarget.Value;
        var outgoing = FindPlayerObject(manager, outgoingTarget.Identity, outgoingTarget.ObjectIndex);
        if (outgoing != null)
        {
            Hide(outgoing, outgoingTarget.ObjectIndex, hiddenObjects);
        }

        RestoreIfHidden(incoming, hiddenObjects);
        if (incomingWasHidden && !hiddenObjects.IsHidden(incoming))
        {
            showTransitionBudget.ConsumeShow();
        }
    }

    private void Clear(HiddenObjectTracker hiddenObjects)
    {
        hiddenObjects.Clear();
        showTransitionBudget.Reset();
        playerKeepRules.Clear();
        keepBudgetStats = default;
        playerAdmissionGate.RequestReset();
        playerTopologyDirtySignal.Clear();
        appliedVisibilityState.Clear();
        playerVisibilityPlanner.Reset();
        latestPlayerVisibilityReconciliation = null;
    }

    #endregion

    #region Culling Rules

    private bool IsCullingEnabled()
    {
        return configuration.HideAllOtherPlayers;
    }

    private bool ShouldSuspendCulling(GameObjectManager* manager)
    {
        return configuration.DisableCullingBelowPlayerCount
            && ObjectTableStats.CountOtherPlayerObjects(manager) < configuration.DisableCullingPlayerCountThreshold;
    }

    private bool ShouldSuspendCullingInDuty()
    {
        return configuration.DisableInDuty && (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56]);
    }

    private List<PlayerKeepCandidate> GetPlayerKeepCandidates(GameObjectManager* manager)
    {
        playerKeepCandidates.Clear();

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            if (!IsPlayerRelatedEvenSlot(index) || IsLocalPlayerReservedSlot(index))
            {
                continue;
            }

            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject == null || gameObject->ObjectKind != ObjectKind.Pc)
            {
                continue;
            }

            var keepDecision = playerKeepRules.GetKeepDecision(gameObject);
            if (keepDecision.Kind != PlayerKeepDecisionKind.None)
            {
                keepDecision = keepDecision.WithViewport(IsScreenVisibleObject(gameObject));
                playerKeepCandidates.Add(new((nint)gameObject, keepDecision, gameObject->EntityId));
            }
        }

        return playerKeepCandidates;
    }

    #endregion

    #region Object Helpers

    private static bool IsPlayerRelatedSlot(int index) => index is >= 0 && index <= MaxPlayerRelatedObjectIndex;

    private static bool IsPlayerRelatedEvenSlot(int index) => IsPlayerRelatedSlot(index) && index % 2 == 0;

    private static bool IsPlayerRelatedOddSlot(int index) => IsPlayerRelatedSlot(index) && index % 2 == 1;

    private static bool IsLocalPlayerReservedSlot(int index) => index is 0 or 1;

    private static GameObject* FindPlayerObject(GameObjectManager* manager, PlayerObjectIdentity identity, int expectedIndex)
    {
        if (manager == null || identity.Address == nint.Zero)
        {
            return null;
        }

        if (expectedIndex >= 0 && expectedIndex < manager->Objects.IndexSorted.Length)
        {
            var expectedObject = manager->Objects.IndexSorted[expectedIndex].Value;
            if (expectedObject != null && expectedObject->ObjectKind == ObjectKind.Pc && identity.Matches(expectedObject))
            {
                return expectedObject;
            }
        }

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject != null && gameObject->ObjectKind == ObjectKind.Pc && identity.Matches(gameObject))
            {
                return gameObject;
            }
        }

        return null;
    }

    private static bool TryGetObjectPosition(GameObject* gameObject, out Vector3 position)
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

    private bool IsScreenVisibleObject(GameObject* gameObject)
    {
        return TryGetObjectPosition(gameObject, out var position) && gameGui.WorldToScreen(position, out _, out var inView) && inView;
    }

    public PlayerKeepBudgetStats GetKeepBudgetStats() => keepBudgetStats;

    public bool ConsumePlayerTopologyDirty() => playerTopologyDirtySignal.Consume();

    private CullingRuntimeMode DetermineRuntimeMode(GameObjectManager* manager)
    {
        if (!IsCullingEnabled())
        {
            return CullingRuntimeMode.Disabled;
        }

        if (!playerState.IsLoaded || manager == null)
        {
            return CullingRuntimeMode.PlayerUnavailable;
        }

        if (ShouldSuspendCullingInDuty())
        {
            return CullingRuntimeMode.SuspendedDuty;
        }

        return ShouldSuspendCulling(manager) ? CullingRuntimeMode.SuspendedLowPlayerCount : CullingRuntimeMode.Active;
    }

    private void EnterInactiveMode(GameObjectManager* manager, HiddenObjectTracker hiddenObjects, bool clearLongTermRuleState)
    {
        if (manager != null)
        {
            RestoreHiddenObjects(manager, hiddenObjects);
        }
        else
        {
            hiddenObjects.Clear();
        }

        ClearPublishedPlayerVisibilityState();
        if (clearLongTermRuleState)
        {
            playerKeepRules.Clear();
        }
    }

    private void ClearPublishedPlayerVisibilityState()
    {
        latestPlayerVisibilityReconciliation = null;
        appliedVisibilityState.Clear();
        playerAdmissionGate.RequestReset();
        playerTopologyDirtySignal.Clear();
        playerVisibilityPlanner.Reset();
        showTransitionBudget.Reset();
        keepBudgetStats = default;
    }

    #endregion
}

internal sealed class ShowTransitionBudget
{
    private const double ShowTransitionsPerSecond = 6.0;
    private const double ShowTransitionCapacity = 1.0;
    private const int MaxShowStartsPerFrame = 1;

    private double tokens = ShowTransitionCapacity;
    private long lastRefill = Environment.TickCount64;
    private int showStartsThisFrame;

    public double CurrentTokens => tokens;

    public void BeginFrame()
    {
        var now = Environment.TickCount64;
        var elapsedMs = Math.Max(0, now - lastRefill);
        tokens = Math.Min(ShowTransitionCapacity, tokens + (elapsedMs / 1000.0 * ShowTransitionsPerSecond));
        lastRefill = now;
        showStartsThisFrame = 0;
    }

    public bool CanStartShow()
    {
        return showStartsThisFrame < MaxShowStartsPerFrame && tokens >= 1.0;
    }

    public void ConsumeShow()
    {
        tokens = Math.Max(0.0, tokens - 1.0);
        showStartsThisFrame++;
    }

    public void Reset()
    {
        tokens = ShowTransitionCapacity;
        lastRefill = Environment.TickCount64;
        showStartsThisFrame = 0;
    }
}
