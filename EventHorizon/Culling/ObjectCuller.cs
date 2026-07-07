using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using EventHorizon.Culling.Rules;
using EventHorizon.Culling.Visibility;
using EventHorizon.Integration.Vfx;
using EventHorizon.Preview;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class ObjectCuller : IDisposable
{
    private const VisibilityFlags PluginCustomProbe = (VisibilityFlags)0x1000;
    private const VisibilityFlags InvisibleFlag = PluginCustomProbe | VisibilityFlags.Nameplate | VisibilityFlags.Model;
    private const string HiddenPlayerVfxPath = StaticVfxResourceRedirector.HiddenPlayerGroundMarkerPath;

    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly StaticVfxController staticVfxController;
    private readonly PlayerKeepRules playerKeepRules;
    private readonly HiddenObjectTracker hiddenObjectTracker;
    private readonly ObjectFadeController fadeController;
    private readonly ShowTransitionBudget showTransitionBudget = new();
    private PlayerKeepBudgetStats keepBudgetStats;
    private PlayerPreviewSnapshot playerPreviewSnapshot = PlayerPreviewSnapshot.Empty;
    private uint? previewSelectedPlayerEntityId;
    private long previewSelectionExpiresAt;
    private int nextPlayerVisibilityPlanRevision;
    private PlayerVisibilityReconciliation? latestPlayerVisibilityReconciliation;

    public ObjectCuller(
        Configuration configuration,
        IPlayerState playerState,
        ICondition condition,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IGameGui gameGui,
        StaticVfxController staticVfxController
    )
    {
        this.configuration = configuration;
        this.playerState = playerState;
        this.condition = condition;
        this.gameGui = gameGui;
        this.staticVfxController = staticVfxController;
        playerKeepRules = new(configuration, objectTable, targetManager);
        hiddenObjectTracker = new();
        fadeController = new(hiddenObjectTracker, InvisibleFlag);
    }

    #region Lifecycle

    public void Update(GameObjectManager* manager)
    {
        if (manager == null)
        {
            Clear();
            return;
        }

        if (!IsCullingEnabled())
        {
            Reset(manager);
            return;
        }

        if (ShouldSuspendCullingInDuty())
        {
            RestoreHiddenObjects(manager);
            ResetFades(manager);
            return;
        }

        if (ShouldSuspendCulling(manager))
        {
            RestoreHiddenObjects(manager);
            ResetFades(manager);
            return;
        }

        playerKeepRules.BeforeUpdate();
        if (!playerState.IsLoaded)
        {
            RestoreHiddenObjects(manager);
            ResetFades(manager);
            return;
        }

        if (!configuration.EnableFadeTransitions && HasActiveFades)
        {
            ResetFades(manager);
        }

        var playerKeepPlan = PlayerKeepPlan.Build(configuration, GetPlayerKeepCandidates(manager));
        var playerVisibilityPlan = PlayerVisibilityPlan.Build(
            ++nextPlayerVisibilityPlanRevision,
            configuration,
            manager,
            playerKeepPlan,
            GetActivePreviewSelectedPlayerEntityId()
        );
        var previewBuilder = PlayerPreviewBuilder.Begin(manager, configuration);
        keepBudgetStats = playerVisibilityPlan.BudgetStats;

        var playerVisibilityReconciliation = PlayerVisibilityReconciler.Reconcile(playerVisibilityPlan, hiddenObjectTracker);
        latestPlayerVisibilityReconciliation = playerVisibilityReconciliation;
        AddPlayerPreviewEntries(manager, playerVisibilityPlan, previewBuilder);
        TickVisibility(manager);
        playerPreviewSnapshot = previewBuilder.Build();
    }

    public void Tick(GameObjectManager* manager)
    {
        if (manager == null)
        {
            Clear();
            return;
        }

        if (!CanTickVisibility())
        {
            Reset(manager);
            return;
        }

        if (latestPlayerVisibilityReconciliation == null)
        {
            return;
        }

        if (!configuration.EnableFadeTransitions && HasActiveFades)
        {
            ResetFades(manager);
        }

        TickVisibility(manager);
    }

    public void Reset(GameObjectManager* manager)
    {
        if (manager == null)
        {
            Clear();
            return;
        }

        RestoreHiddenObjects(manager);
        ResetFades(manager);
        Clear();
    }

    private void RestoreHiddenObjects(GameObjectManager* manager)
    {
        hiddenObjectTracker.RestoreAll(manager);
        ClearHiddenPlayerVfx();
    }

    public void ClearRuleState()
    {
        playerKeepRules.Clear();
    }

    public void RecordChatMessage(IChatMessage message)
    {
        playerKeepRules.RecordChatMessage(message);
    }

    public void RefreshPlayerPreview(GameObjectManager* manager)
    {
        if (manager == null || !IsCullingEnabled() || !playerState.IsLoaded || playerPreviewSnapshot.Players.Count == 0)
        {
            return;
        }

        var previousPlayers = new Dictionary<uint, PlayerPreviewEntry>();
        foreach (var player in playerPreviewSnapshot.Players)
        {
            previousPlayers[player.EntityId] = player;
        }

        var previewBuilder = PlayerPreviewBuilder.Begin(manager, configuration);
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

            if (previousPlayers.TryGetValue(gameObject->EntityId, out var previousPlayer))
            {
                previewBuilder.Add(gameObject, index, previousPlayer);
            }
        }

        playerPreviewSnapshot = previewBuilder.Build();
    }

    public bool SetPreviewSelectedPlayer(uint? entityId)
    {
        var previousEntityId = GetActivePreviewSelectedPlayerEntityId();
        if (entityId.HasValue)
        {
            previewSelectedPlayerEntityId = entityId.Value;
            previewSelectionExpiresAt = Environment.TickCount64 + PlayerPreviewConstants.SelectionVisibilityLeaseMs;
        }
        else
        {
            previewSelectedPlayerEntityId = null;
            previewSelectionExpiresAt = 0;
        }

        return previousEntityId != GetActivePreviewSelectedPlayerEntityId();
    }

    public void Dispose()
    {
        Reset(GameObjectManager.Instance());
    }

    #endregion

    #region Visibility

    private void Hide(GameObject* gameObject)
    {
        hiddenObjectTracker.Hide(gameObject, InvisibleFlag);
    }

    private void RestoreIfHidden(GameObject* gameObject)
    {
        hiddenObjectTracker.RestoreIfHidden(gameObject);
    }

    private void ApplyPlayerVisibilityReconciliation(GameObjectManager* manager, PlayerVisibilityReconciliation reconciliation)
    {
        foreach (var action in reconciliation.Actions)
        {
            switch (action.Kind)
            {
                case PlayerVisibilityActionKind.Show:
                    ApplyShowAction(manager, action.Intent);
                    break;
                case PlayerVisibilityActionKind.Hide:
                    ApplyHideAction(manager, action.Intent);
                    break;
                case PlayerVisibilityActionKind.Swap:
                    ApplySwapAction(manager, action);
                    break;
            }
        }
    }

    private void TickVisibility(GameObjectManager* manager)
    {
        if (latestPlayerVisibilityReconciliation == null)
        {
            return;
        }

        showTransitionBudget.BeginFrame();
        ApplyPlayerVisibilityReconciliation(manager, latestPlayerVisibilityReconciliation);
        ApplyNonPlayerVisibility(manager);
        PruneMissingHiddenObjects(manager);
        PruneMissingFades(manager);
        UpdateHiddenPlayerVfx(manager);
    }

    private void ApplyNonPlayerVisibility(GameObjectManager* manager)
    {
        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject == null || gameObject->ObjectKind == ObjectKind.Pc)
            {
                continue;
            }

            if (ShouldHideNonPlayerSlotObject(manager, gameObject, index))
            {
                Hide(gameObject);
            }
            else
            {
                RestoreIfHidden(gameObject);
            }
        }
    }

    private void ApplyShowAction(GameObjectManager* manager, PlayerVisibilityIntent intent)
    {
        var gameObject = FindPlayerObject(manager, intent.Identity, intent.ObjectIndex);
        if (gameObject == null)
        {
            return;
        }

        var wasHidden = hiddenObjectTracker.IsHidden(gameObject);
        if (wasHidden && !showTransitionBudget.CanStartShow())
        {
            return;
        }

        ApplyVisibility(gameObject, shouldHide: false);
        if (wasHidden && !hiddenObjectTracker.IsHidden(gameObject))
        {
            showTransitionBudget.ConsumeShow();
        }
    }

    private void ApplyHideAction(GameObjectManager* manager, PlayerVisibilityIntent intent)
    {
        var gameObject = FindPlayerObject(manager, intent.Identity, intent.ObjectIndex);
        if (gameObject != null)
        {
            ApplyVisibility(gameObject, shouldHide: true);
        }
    }

    private void ApplySwapAction(GameObjectManager* manager, PlayerVisibilityAction action)
    {
        if (!action.PairedIntent.HasValue)
        {
            ApplyShowAction(manager, action.Intent);
            return;
        }

        var incoming = FindPlayerObject(manager, action.Intent.Identity, action.Intent.ObjectIndex);
        if (incoming == null)
        {
            return;
        }

        var incomingWasHidden = hiddenObjectTracker.IsHidden(incoming);
        if (incomingWasHidden && !showTransitionBudget.CanStartShow())
        {
            return;
        }

        var outgoingIntent = action.PairedIntent.Value;
        var outgoing = FindPlayerObject(manager, outgoingIntent.Identity, outgoingIntent.ObjectIndex);
        if (outgoing != null)
        {
            ApplyVisibility(outgoing, shouldHide: true);
        }

        ApplyVisibility(incoming, shouldHide: false);
        if (incomingWasHidden && !hiddenObjectTracker.IsHidden(incoming))
        {
            showTransitionBudget.ConsumeShow();
        }
    }

    private void ApplyVisibility(GameObject* gameObject, bool shouldHide)
    {
        if (UpdateFade(gameObject, shouldHide))
        {
            return;
        }

        if (shouldHide)
        {
            Hide(gameObject);
        }
        else
        {
            RestoreIfHidden(gameObject);
        }
    }

    private static void AddPlayerPreviewEntries(
        GameObjectManager* manager,
        PlayerVisibilityPlan playerVisibilityPlan,
        PlayerPreviewBuilder previewBuilder
    )
    {
        foreach (var intent in playerVisibilityPlan.Intents)
        {
            if (IsLocalPlayerReservedSlot(intent.ObjectIndex) || !IsPlayerRelatedEvenSlot(intent.ObjectIndex))
            {
                continue;
            }

            var gameObject = FindPlayerObject(manager, intent.Identity, intent.ObjectIndex);
            if (gameObject != null)
            {
                previewBuilder.Add(gameObject, intent.ObjectIndex, intent.Decision, !intent.DesiredVisible, intent.CutByBudget);
            }
        }
    }

    private bool UpdateFade(GameObject* gameObject, bool shouldHide)
    {
        if (!configuration.EnableFadeTransitions)
        {
            return false;
        }

        return fadeController.Update(gameObject, shouldHide);
    }

    private void PruneMissingHiddenObjects(GameObjectManager* manager)
    {
        hiddenObjectTracker.PruneMissing(manager);
    }

    private void PruneMissingFades(GameObjectManager* manager)
    {
        fadeController.PruneMissing(manager);
    }

    private void ResetFades(GameObjectManager* manager)
    {
        fadeController.Reset(manager);
    }

    private void Clear()
    {
        hiddenObjectTracker.Clear();
        fadeController.Clear();
        showTransitionBudget.Reset();
        ClearHiddenPlayerVfx();
        playerKeepRules.Clear();
        keepBudgetStats = default;
        playerPreviewSnapshot = PlayerPreviewSnapshot.Empty;
        previewSelectedPlayerEntityId = null;
        previewSelectionExpiresAt = 0;
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

    private bool ShouldHideNonPlayerSlotObject(GameObjectManager* manager, GameObject* gameObject, int index)
    {
        if (IsLocalPlayerReservedSlot(index))
        {
            return false;
        }

        if (IsPlayerRelatedEvenSlot(index))
        {
            if (gameObject->ObjectKind != ObjectKind.BattleNpc)
            {
                return false;
            }

            var owner = FindPlayerOwner(manager, gameObject);
            return owner != null && IsHiddenByThisPlugin(owner);
        }

        if (IsPlayerRelatedOddSlot(index))
        {
            return gameObject->ObjectKind switch
            {
                ObjectKind.Companion => configuration.HideOtherPlayerCompanions,
                ObjectKind.Ornament => configuration.HideOtherPlayerOrnaments,
                _ => false,
            };
        }

        return false;
    }

    private List<PlayerKeepCandidate> GetPlayerKeepCandidates(GameObjectManager* manager)
    {
        var candidates = new List<PlayerKeepCandidate>();

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
                candidates.Add(new((nint)gameObject, keepDecision, gameObject->EntityId));
            }
        }

        return candidates;
    }

    private uint? GetActivePreviewSelectedPlayerEntityId()
    {
        if (!previewSelectedPlayerEntityId.HasValue)
        {
            return null;
        }

        if (Environment.TickCount64 <= previewSelectionExpiresAt)
        {
            return previewSelectedPlayerEntityId;
        }

        previewSelectedPlayerEntityId = null;
        previewSelectionExpiresAt = 0;
        return null;
    }

    #endregion

    #region Object Helpers

    private static bool IsPlayerRelatedSlot(int index) => index is >= 0 and <= 199;

    private static bool IsPlayerRelatedEvenSlot(int index) => IsPlayerRelatedSlot(index) && index % 2 == 0;

    private static bool IsPlayerRelatedOddSlot(int index) => IsPlayerRelatedSlot(index) && index % 2 == 1;

    private static bool IsLocalPlayerReservedSlot(int index) => index is 0 or 1;

    private static GameObject* FindPlayerOwner(GameObjectManager* manager, GameObject* gameObject)
    {
        if (manager == null || gameObject == null || gameObject->OwnerId == 0)
        {
            return null;
        }

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            if (!IsPlayerRelatedEvenSlot(index))
            {
                continue;
            }

            var owner = manager->Objects.IndexSorted[index].Value;
            if (owner != null && owner->ObjectKind == ObjectKind.Pc && owner->EntityId == gameObject->OwnerId)
            {
                return owner;
            }
        }

        return null;
    }

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

    private bool IsHiddenByThisPlugin(GameObject* gameObject)
    {
        return hiddenObjectTracker.IsHidden(gameObject);
    }

    private void UpdateHiddenPlayerVfx(GameObjectManager* manager)
    {
        if (!configuration.EnableHiddenPlayerGroundMarker || manager == null)
        {
            ClearHiddenPlayerVfx();
            return;
        }

        var hiddenPlayerAddresses = new List<nint>();
        var visibleHiddenPlayerIds = new HashSet<ulong>();

        hiddenObjectTracker.CollectHiddenPlayerAddresses(manager, hiddenPlayerAddresses);

        foreach (var address in hiddenPlayerAddresses)
        {
            var gameObject = (GameObject*)address;
            if (!TryGetScreenVisiblePosition(gameObject, out var position))
            {
                continue;
            }

            var gameObjectId = (ulong)gameObject->GetGameObjectId();
            visibleHiddenPlayerIds.Add(gameObjectId);
            staticVfxController.ShowOrUpdate(gameObjectId, HiddenPlayerVfxPath, position, gameObject->Rotation);
        }

        staticVfxController.PruneExcept(visibleHiddenPlayerIds);
    }

    private bool TryGetScreenVisiblePosition(GameObject* gameObject, out Vector3 screenVisiblePosition)
    {
        screenVisiblePosition = default;
        if (gameObject == null || gameObject->VirtualTable == null)
        {
            return false;
        }

        var position = gameObject->GetPosition();
        if (position == null)
        {
            return false;
        }

        screenVisiblePosition = (Vector3)(*position);
        return gameGui.WorldToScreen(screenVisiblePosition, out _, out var inView) && inView;
    }

    private void ClearHiddenPlayerVfx()
    {
        staticVfxController.Clear();
    }

    public int GetHiddenPlayerCount() => hiddenObjectTracker.HiddenPlayerCount;

    public PlayerKeepBudgetStats GetKeepBudgetStats() => keepBudgetStats;

    public PlayerPreviewSnapshot GetPlayerPreviewSnapshot() => playerPreviewSnapshot;

    public bool NeedsDynamicRefresh()
    {
        return IsCullingEnabled() && !ShouldSuspendCullingInDuty() && playerState.IsLoaded;
    }

    private bool CanTickVisibility()
    {
        return IsCullingEnabled() && !ShouldSuspendCullingInDuty() && playerState.IsLoaded;
    }

    public bool HasActiveFades => fadeController.HasActiveFades;

    #endregion
}
