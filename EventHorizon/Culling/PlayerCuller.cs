using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
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
    PlayerAdmissionGate admissionGate,
    IPluginLog log
)
{
    private const VisibilityFlags InvisibleFlag = HiddenObjectTracker.PluginHiddenFlags;

    private readonly Configuration configuration = configuration;
    private readonly IPlayerState playerState = playerState;
    private readonly IObjectTable objectTable = objectTable;
    private readonly IGameGui gameGui = gameGui;
    private readonly PlayerAdmissionGate admissionGate = admissionGate;
    private readonly PlayerKeepRules playerKeepRules = new(configuration, objectTable, targetManager);
    private readonly PlayerKeepPlan playerKeepPlan = new();
    private readonly List<PlayerKeepCandidate> playerKeepCandidates = [];
    private readonly PlayerVisibilityPlanner playerVisibilityPlanner = new(exception =>
        log.Error(exception, "Player visibility selection failed; using legacy fallback.")
    );
    private readonly ShowTransitionBudget showTransitionBudget = new();
    private readonly List<PlayerVisibilityAction> pendingVisibilityActions = [];
    private PlayerVisibilityFrameState? latestPlayerVisibilityFrame;

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
            Math.Clamp(configuration.KeepNearbyPlayersRange, PlayerKeepRuleSettings.NearbyRangeMin, PlayerKeepRuleSettings.NearbyRangeMax),
            hiddenObjects
        );
        pendingVisibilityActions.Clear();
        pendingVisibilityActions.AddRange(frameState.Actions);
        Volatile.Write(ref latestPlayerVisibilityFrame, frameState);
        playerVisibilityPlanner.Commit(frameState);
    }

    public void Tick(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
        if (manager == null)
        {
            ClearRuntimeState();
            return;
        }

        if (Volatile.Read(ref latestPlayerVisibilityFrame) == null)
        {
            return;
        }

        TickVisibility(manager, hiddenObjects);
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
        var frame = Volatile.Read(ref latestPlayerVisibilityFrame);
        if (manager == null || !IsCullingEnabled() || !playerState.IsLoaded || frame == null)
        {
            return;
        }

        preview.Refresh(manager, frame.ActiveTarget);
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

    private void ApplyPendingPlayerVisibilityActions(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
        var retryCount = 0;
        var actionCount = pendingVisibilityActions.Count;
        for (var index = 0; index < actionCount; index++)
        {
            var action = pendingVisibilityActions[index];
            if (ApplyPlayerVisibilityAction(manager, action, hiddenObjects) == PlayerVisibilityActionResult.Retry)
            {
                pendingVisibilityActions[retryCount++] = action;
            }
        }

        pendingVisibilityActions.RemoveRange(retryCount, pendingVisibilityActions.Count - retryCount);
    }

    private PlayerVisibilityActionResult ApplyPlayerVisibilityAction(
        GameObjectManager* manager,
        PlayerVisibilityAction action,
        HiddenObjectTracker hiddenObjects
    )
    {
        return action.Kind switch
        {
            PlayerVisibilityActionKind.Show => ApplyShowAction(manager, action.Target, hiddenObjects),
            PlayerVisibilityActionKind.Hide => ApplyHideAction(manager, action.Target, hiddenObjects),
            PlayerVisibilityActionKind.Swap => ApplySwapAction(manager, action, hiddenObjects),
            _ => PlayerVisibilityActionResult.Completed,
        };
    }

    private void TickVisibility(GameObjectManager* manager, HiddenObjectTracker hiddenObjects)
    {
        var frame = Volatile.Read(ref latestPlayerVisibilityFrame);
        if (frame == null)
        {
            return;
        }

        showTransitionBudget.BeginFrame();
        admissionGate.Reconcile(manager, frame.ActiveTarget, hiddenObjects, showTransitionBudget);
        ApplyPendingPlayerVisibilityActions(manager, hiddenObjects);
    }

    private PlayerVisibilityActionResult ApplyShowAction(
        GameObjectManager* manager,
        PlayerVisibilityTarget target,
        HiddenObjectTracker hiddenObjects
    )
    {
        var gameObject = FindPlayerObject(manager, target.Identity, target.ObjectIndex);
        if (gameObject == null)
        {
            return PlayerVisibilityActionResult.Completed;
        }

        var wasHidden = hiddenObjects.IsHidden(gameObject);
        if (wasHidden && !showTransitionBudget.CanStartShow())
        {
            return PlayerVisibilityActionResult.Retry;
        }

        RestoreIfHidden(gameObject, hiddenObjects);
        if (wasHidden && !hiddenObjects.IsHidden(gameObject))
        {
            showTransitionBudget.ConsumeShow();
        }

        return PlayerVisibilityActionResult.Completed;
    }

    private static PlayerVisibilityActionResult ApplyHideAction(
        GameObjectManager* manager,
        PlayerVisibilityTarget target,
        HiddenObjectTracker hiddenObjects
    )
    {
        var gameObject = FindPlayerObject(manager, target.Identity, target.ObjectIndex);
        if (gameObject != null)
        {
            Hide(gameObject, target.ObjectIndex, hiddenObjects);
        }

        return PlayerVisibilityActionResult.Completed;
    }

    private PlayerVisibilityActionResult ApplySwapAction(
        GameObjectManager* manager,
        PlayerVisibilityAction action,
        HiddenObjectTracker hiddenObjects
    )
    {
        if (!action.PairedTarget.HasValue)
        {
            return ApplyShowAction(manager, action.Target, hiddenObjects);
        }

        var incoming = FindPlayerObject(manager, action.Target.Identity, action.Target.ObjectIndex);
        if (incoming == null)
        {
            return PlayerVisibilityActionResult.Completed;
        }

        var incomingWasHidden = hiddenObjects.IsHidden(incoming);
        if (incomingWasHidden && !showTransitionBudget.CanStartShow())
        {
            return PlayerVisibilityActionResult.Retry;
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

        return PlayerVisibilityActionResult.Completed;
    }

    public void ClearRuntimeState()
    {
        showTransitionBudget.Reset();
        playerVisibilityPlanner.Reset();
        pendingVisibilityActions.Clear();
        Volatile.Write(ref latestPlayerVisibilityFrame, null);
    }

    public void ClearAllState()
    {
        playerKeepRules.Clear();
        ClearRuntimeState();
    }

    #endregion

    private enum PlayerVisibilityActionResult
    {
        Completed,
        Retry,
    }

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

    #endregion

    internal sealed class ShowTransitionBudget
    {
        private const double ShowTransitionsPerSecond = 12.0;
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
