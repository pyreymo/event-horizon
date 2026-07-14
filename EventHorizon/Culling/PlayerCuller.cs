using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using EventHorizon.Preview;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class PlayerCuller(
    Configuration configuration,
    IPlayerState playerState,
    IObjectTable objectTable,
    ITargetManager targetManager,
    IGameGui gameGui,
    IPluginLog log
)
{
    private const VisibilityFlags InvisibleFlag = HiddenObjectTracker.PluginHiddenFlags;

    private readonly Configuration configuration = configuration;
    private readonly IPlayerState playerState = playerState;
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
    private PlayerVisibilityAction[]? latestPlayerVisibilityActions;

    #region Lifecycle

    public void Update(GameObjectManager* manager, HiddenObjectTracker hiddenObjects, PlayerPreview preview)
    {
        if (manager == null)
        {
            ClearRuntimeState();
            return;
        }

        playerKeepRules.BeforeUpdate();

        playerKeepPlan.Update(configuration, GetPlayerKeepCandidates(manager));
        var frameState = playerVisibilityPlanner.BuildFrame(
            manager,
            playerKeepPlan,
            preview.ActiveSelectedPlayerEntityId,
            configuration.LimitVisiblePlayerCount,
            configuration.VisiblePlayerCountLimit,
            objectTable.LocalPlayer?.Position,
            hiddenObjects
        );
        appliedVisibilityState.Publish(frameState);
        latestPlayerVisibilityActions = frameState.Actions;
        playerVisibilityPlanner.Commit(frameState);
    }

    public void Tick(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
        if (manager == null)
        {
            ClearRuntimeState();
            return;
        }

        if (latestPlayerVisibilityActions == null)
        {
            return;
        }

        TickVisibility(manager, hiddenObjects);
    }

    public void ApplyAdmissionGate(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
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
            (slot, identity) =>
            {
                var gameObject = manager->Objects.IndexSorted[slot].Value;
                if (!identity.Matches(gameObject))
                {
                    throw new InvalidOperationException("Admission slot identity changed before the hard hide could be applied.");
                }

                Hide(gameObject, slot, hiddenObjects);
            }
        );
        playerTopologyDirtySignal.MarkFrom(result);
    }

    public void ResetAdmissionGate()
    {
        playerAdmissionGate.ResetTracking();
        playerTopologyDirtySignal.Clear();
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
        PlayerVisibilityAction[] actions,
        HiddenObjectTracker hiddenObjects
    )
    {
        foreach (var action in actions)
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
        if (latestPlayerVisibilityActions == null)
        {
            return;
        }

        showTransitionBudget.BeginFrame();
        ApplyPlayerVisibilityReconciliation(manager, latestPlayerVisibilityActions, hiddenObjects);
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

    public void ClearRuntimeState()
    {
        showTransitionBudget.Reset();
        playerAdmissionGate.RequestReset();
        playerTopologyDirtySignal.Clear();
        appliedVisibilityState.Clear();
        playerVisibilityPlanner.Reset();
        latestPlayerVisibilityActions = null;
    }

    public void ClearAllState()
    {
        playerKeepRules.Clear();
        ClearRuntimeState();
    }

    #endregion

    #region Culling Rules

    private bool IsCullingEnabled()
    {
        return configuration.HideAllOtherPlayers;
    }

    private List<PlayerKeepCandidate> GetPlayerKeepCandidates(GameObjectManager* manager)
    {
        playerKeepCandidates.Clear();

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            if (!CharacterObjectSlots.IsEvenSlot(index))
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

    public bool ConsumePlayerTopologyDirty() => playerTopologyDirtySignal.Consume();

    #endregion

    private sealed class ShowTransitionBudget
    {
        private const double ShowTransitionsPerSecond = 6.0;
        private const double ShowTransitionCapacity = 1.0;
        private const int MaxShowStartsPerFrame = 1;

        private double tokens = ShowTransitionCapacity;
        private long lastRefill = Environment.TickCount64;
        private int showStartsThisFrame;

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
}
