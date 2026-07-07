using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const int MaxMaintainedPlayerActionsPerFrame = 24;
    private const int HiddenPlayerVfxUpdatesPerFrame = 4;
    private const int HiddenPlayerVfxPruneIntervalMs = 500;
    private const VisibilityFlags PluginCustomProbe = (VisibilityFlags)0x1000;
    private const VisibilityFlags InvisibleFlag = PluginCustomProbe | VisibilityFlags.Nameplate | VisibilityFlags.Model;
    private const string HiddenPlayerVfxPath = StaticVfxResourceRedirector.HiddenPlayerGroundMarkerPath;

    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly StaticVfxController staticVfxController;
    private readonly PlayerKeepRules playerKeepRules;
    private readonly PlayerKeepPlan playerKeepPlan = new();
    private readonly List<PlayerKeepCandidate> playerKeepCandidates = [];
    private readonly HiddenObjectTracker hiddenObjectTracker;
    private readonly ObjectFadeController fadeController;
    private readonly PlayerVisibilityReconciler playerVisibilityReconciler = new();
    private readonly ShowTransitionBudget showTransitionBudget = new();
    private readonly List<PlayerVisibilityIntent> playerVisibilityIntents = [];
    private readonly List<nint> hiddenPlayerVfxAddresses = [];
    private readonly HashSet<ulong> currentHiddenPlayerVfxIds = [];
    private PlayerKeepBudgetStats keepBudgetStats;
    private PlayerPreviewSnapshot playerPreviewSnapshot = PlayerPreviewSnapshot.Empty;
    private uint? previewSelectedPlayerEntityId;
    private long previewSelectionExpiresAt;
    private long nextHiddenPlayerVfxPrune;
    private int nextPlayerVisibilityPlanRevision;
    private int nextMaintainedVisibilityActionIndex;
    private int nextHiddenPlayerVfxIndex;
    private PlayerVisibilityPlan? latestPlayerVisibilityPlan;
    private PlayerVisibilityReconciliation? latestPlayerVisibilityReconciliation;

    public CullingPerformanceTrace LastUpdateTrace { get; private set; }
    public CullingPerformanceTrace LastTickTrace { get; private set; }

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

    public void Update(GameObjectManager* manager, bool refreshPlayerPreview)
    {
        var start = Stopwatch.GetTimestamp();
        var phaseStart = start;

        if (manager == null)
        {
            Clear();
            LastUpdateTrace = CreateTrace(
                isRefresh: true,
                refreshPlayerPreview,
                start,
                phaseStart,
                default,
                default,
                default,
                default,
                default
            );
            return;
        }

        if (!IsCullingEnabled())
        {
            Reset(manager);
            LastUpdateTrace = CreateTrace(
                isRefresh: true,
                refreshPlayerPreview,
                start,
                phaseStart,
                default,
                default,
                default,
                default,
                default
            );
            return;
        }

        if (ShouldSuspendCullingInDuty())
        {
            RestoreHiddenObjects(manager);
            ResetFades(manager);
            LastUpdateTrace = CreateTrace(
                isRefresh: true,
                refreshPlayerPreview,
                start,
                phaseStart,
                default,
                default,
                default,
                default,
                default
            );
            return;
        }

        if (ShouldSuspendCulling(manager))
        {
            RestoreHiddenObjects(manager);
            ResetFades(manager);
            LastUpdateTrace = CreateTrace(
                isRefresh: true,
                refreshPlayerPreview,
                start,
                phaseStart,
                default,
                default,
                default,
                default,
                default
            );
            return;
        }

        playerKeepRules.BeforeUpdate();
        if (!playerState.IsLoaded)
        {
            RestoreHiddenObjects(manager);
            ResetFades(manager);
            LastUpdateTrace = CreateTrace(
                isRefresh: true,
                refreshPlayerPreview,
                start,
                phaseStart,
                default,
                default,
                default,
                default,
                default
            );
            return;
        }

        if (!configuration.EnableFadeTransitions && HasActiveFades)
        {
            ResetFades(manager);
        }

        var guardTicks = Stopwatch.GetTimestamp() - phaseStart;
        phaseStart = Stopwatch.GetTimestamp();
        playerKeepPlan.Update(configuration, GetPlayerKeepCandidates(manager));
        var keepPlanTicks = Stopwatch.GetTimestamp() - phaseStart;
        phaseStart = Stopwatch.GetTimestamp();
        var playerVisibilityPlan = PlayerVisibilityPlan.Build(
            ++nextPlayerVisibilityPlanRevision,
            configuration,
            manager,
            playerKeepPlan,
            GetActivePreviewSelectedPlayerEntityId(),
            playerVisibilityIntents
        );
        keepBudgetStats = playerVisibilityPlan.BudgetStats;
        latestPlayerVisibilityPlan = playerVisibilityPlan;
        var visibilityPlanTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        var playerVisibilityReconciliation = playerVisibilityReconciler.Reconcile(playerVisibilityPlan, hiddenObjectTracker);
        latestPlayerVisibilityReconciliation = playerVisibilityReconciliation;
        var reconcileTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        var previewBuilder = refreshPlayerPreview ? PlayerPreviewBuilder.Begin(manager, configuration) : null;
        if (previewBuilder != null)
        {
            AddPlayerPreviewEntries(manager, playerVisibilityPlan, previewBuilder);
        }
        var previewTicks = Stopwatch.GetTimestamp() - phaseStart;

        TickVisibility(manager);
        var tickTrace = LastTickTrace.Tick;
        if (previewBuilder != null)
        {
            phaseStart = Stopwatch.GetTimestamp();
            playerPreviewSnapshot = previewBuilder.Build();
            previewTicks += Stopwatch.GetTimestamp() - phaseStart;
        }

        LastUpdateTrace = new CullingPerformanceTrace(
            IsRefresh: true,
            RefreshPlayerPreview: refreshPlayerPreview,
            ActionCount: playerVisibilityReconciliation.Actions.Count,
            PendingShowCount: playerVisibilityReconciliation.PendingShowCount,
            PendingHideCount: playerVisibilityReconciliation.PendingHideCount,
            TotalTicks: Stopwatch.GetTimestamp() - start,
            GuardTicks: guardTicks,
            KeepPlanTicks: keepPlanTicks,
            VisibilityPlanTicks: visibilityPlanTicks,
            ReconcileTicks: reconcileTicks,
            PreviewTicks: previewTicks,
            Tick: tickTrace
        );
    }

    public void Tick(GameObjectManager* manager)
    {
        var start = Stopwatch.GetTimestamp();
        var phaseStart = start;

        if (manager == null)
        {
            Clear();
            LastTickTrace = CreateTrace(
                isRefresh: false,
                refreshPlayerPreview: false,
                start,
                phaseStart,
                default,
                default,
                default,
                default,
                default
            );
            return;
        }

        if (!CanTickVisibility())
        {
            Reset(manager);
            LastTickTrace = CreateTrace(
                isRefresh: false,
                refreshPlayerPreview: false,
                start,
                phaseStart,
                default,
                default,
                default,
                default,
                default
            );
            return;
        }

        if (latestPlayerVisibilityReconciliation == null)
        {
            LastTickTrace = CreateTrace(
                isRefresh: false,
                refreshPlayerPreview: false,
                start,
                phaseStart,
                default,
                default,
                default,
                default,
                default
            );
            return;
        }

        if (!configuration.EnableFadeTransitions && HasActiveFades)
        {
            ResetFades(manager);
        }

        var guardTicks = Stopwatch.GetTimestamp() - phaseStart;
        TickVisibility(manager);
        var tickTrace = LastTickTrace.Tick;
        LastTickTrace = new CullingPerformanceTrace(
            IsRefresh: false,
            RefreshPlayerPreview: false,
            ActionCount: latestPlayerVisibilityReconciliation.Actions.Count,
            PendingShowCount: latestPlayerVisibilityReconciliation.PendingShowCount,
            PendingHideCount: latestPlayerVisibilityReconciliation.PendingHideCount,
            TotalTicks: Stopwatch.GetTimestamp() - start,
            GuardTicks: guardTicks,
            KeepPlanTicks: 0,
            VisibilityPlanTicks: 0,
            ReconcileTicks: 0,
            PreviewTicks: 0,
            Tick: tickTrace
        );
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
        if (manager == null || !IsCullingEnabled() || !playerState.IsLoaded || latestPlayerVisibilityPlan == null)
        {
            return;
        }

        var previewBuilder = PlayerPreviewBuilder.Begin(manager, configuration);
        AddPlayerPreviewEntries(manager, latestPlayerVisibilityPlan, previewBuilder);
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

    private void Hide(GameObject* gameObject, int objectIndex)
    {
        hiddenObjectTracker.Hide(gameObject, InvisibleFlag, objectIndex);
    }

    private void RestoreIfHidden(GameObject* gameObject)
    {
        hiddenObjectTracker.RestoreIfHidden(gameObject);
    }

    private void ApplyPlayerVisibilityReconciliation(GameObjectManager* manager, PlayerVisibilityReconciliation reconciliation)
    {
        foreach (var action in reconciliation.Actions)
        {
            if (action.Reason != PlayerVisibilityActionReason.Maintain)
            {
                ApplyPlayerVisibilityAction(manager, action);
            }
        }

        ApplyMaintainedPlayerVisibilityActions(manager, reconciliation.Actions);
    }

    private void ApplyMaintainedPlayerVisibilityActions(GameObjectManager* manager, IReadOnlyList<PlayerVisibilityAction> actions)
    {
        if (!HasActiveFades)
        {
            nextMaintainedVisibilityActionIndex = 0;
            return;
        }

        var maintainedCount = CountMaintainedPlayerVisibilityActions(actions);
        if (maintainedCount == 0)
        {
            nextMaintainedVisibilityActionIndex = 0;
            return;
        }

        var startIndex = nextMaintainedVisibilityActionIndex % maintainedCount;
        var processedCount = 0;
        ApplyMaintainedPlayerVisibilityActions(manager, actions, startIndex, ref processedCount);
        if (processedCount < MaxMaintainedPlayerActionsPerFrame && startIndex > 0)
        {
            ApplyMaintainedPlayerVisibilityActions(manager, actions, 0, ref processedCount, stopBeforeMaintainedIndex: startIndex);
        }

        nextMaintainedVisibilityActionIndex = (startIndex + processedCount) % maintainedCount;
    }

    private void ApplyMaintainedPlayerVisibilityActions(
        GameObjectManager* manager,
        IReadOnlyList<PlayerVisibilityAction> actions,
        int startMaintainedIndex,
        ref int processedCount,
        int? stopBeforeMaintainedIndex = null
    )
    {
        var maintainedIndex = 0;
        foreach (var action in actions)
        {
            if (action.Reason != PlayerVisibilityActionReason.Maintain)
            {
                continue;
            }

            if (stopBeforeMaintainedIndex.HasValue && maintainedIndex >= stopBeforeMaintainedIndex.Value)
            {
                return;
            }

            if (maintainedIndex++ < startMaintainedIndex)
            {
                continue;
            }

            if (!fadeController.IsFading(action.Intent.Identity))
            {
                continue;
            }

            ApplyPlayerVisibilityAction(manager, action);
            processedCount++;
            if (processedCount >= MaxMaintainedPlayerActionsPerFrame)
            {
                return;
            }
        }
    }

    private static int CountMaintainedPlayerVisibilityActions(IReadOnlyList<PlayerVisibilityAction> actions)
    {
        var count = 0;
        foreach (var action in actions)
        {
            if (action.Reason == PlayerVisibilityActionReason.Maintain)
            {
                count++;
            }
        }

        return count;
    }

    private void ApplyPlayerVisibilityAction(GameObjectManager* manager, PlayerVisibilityAction action)
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

    private void TickVisibility(GameObjectManager* manager)
    {
        if (latestPlayerVisibilityReconciliation == null)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var phaseStart = start;
        showTransitionBudget.BeginFrame();
        ApplyPlayerVisibilityReconciliation(manager, latestPlayerVisibilityReconciliation);
        var playerActionsTicks = Stopwatch.GetTimestamp() - phaseStart;
        phaseStart = Stopwatch.GetTimestamp();
        ApplyNonPlayerVisibility(manager);
        var nonPlayerTicks = Stopwatch.GetTimestamp() - phaseStart;
        phaseStart = Stopwatch.GetTimestamp();
        PruneMissingHiddenObjects(manager);
        var pruneHiddenTicks = Stopwatch.GetTimestamp() - phaseStart;
        phaseStart = Stopwatch.GetTimestamp();
        PruneMissingFades(manager);
        var pruneFadesTicks = Stopwatch.GetTimestamp() - phaseStart;
        phaseStart = Stopwatch.GetTimestamp();
        UpdateHiddenPlayerVfx(manager);
        var hiddenVfxTicks = Stopwatch.GetTimestamp() - phaseStart;
        LastTickTrace = new CullingPerformanceTrace(
            IsRefresh: false,
            RefreshPlayerPreview: false,
            ActionCount: latestPlayerVisibilityReconciliation.Actions.Count,
            PendingShowCount: latestPlayerVisibilityReconciliation.PendingShowCount,
            PendingHideCount: latestPlayerVisibilityReconciliation.PendingHideCount,
            TotalTicks: Stopwatch.GetTimestamp() - start,
            GuardTicks: 0,
            KeepPlanTicks: 0,
            VisibilityPlanTicks: 0,
            ReconcileTicks: 0,
            PreviewTicks: 0,
            Tick: new CullingTickPerformanceTrace(
                latestPlayerVisibilityReconciliation.Actions.Count,
                Stopwatch.GetTimestamp() - start,
                playerActionsTicks,
                nonPlayerTicks,
                pruneHiddenTicks,
                pruneFadesTicks,
                hiddenVfxTicks
            )
        );
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
                Hide(gameObject, index);
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

        ApplyVisibility(gameObject, shouldHide: false, intent.ObjectIndex);
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
            ApplyVisibility(gameObject, shouldHide: true, intent.ObjectIndex);
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
            ApplyVisibility(outgoing, shouldHide: true, outgoingIntent.ObjectIndex);
        }

        ApplyVisibility(incoming, shouldHide: false, action.Intent.ObjectIndex);
        if (incomingWasHidden && !hiddenObjectTracker.IsHidden(incoming))
        {
            showTransitionBudget.ConsumeShow();
        }
    }

    private void ApplyVisibility(GameObject* gameObject, bool shouldHide, int objectIndex)
    {
        if (UpdateFade(gameObject, shouldHide, objectIndex))
        {
            return;
        }

        if (shouldHide)
        {
            Hide(gameObject, objectIndex);
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

    private bool UpdateFade(GameObject* gameObject, bool shouldHide, int objectIndex)
    {
        if (!configuration.EnableFadeTransitions)
        {
            return false;
        }

        return fadeController.Update(gameObject, shouldHide, objectIndex);
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
        nextMaintainedVisibilityActionIndex = 0;
        nextHiddenPlayerVfxIndex = 0;
        nextHiddenPlayerVfxPrune = 0;
        latestPlayerVisibilityPlan = null;
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
                playerKeepCandidates.Add(new((nint)gameObject, keepDecision, gameObject->EntityId));
            }
        }

        return playerKeepCandidates;
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
            nextHiddenPlayerVfxIndex = 0;
            nextHiddenPlayerVfxPrune = 0;
            return;
        }

        hiddenPlayerVfxAddresses.Clear();
        hiddenObjectTracker.CollectHiddenPlayerAddresses(manager, hiddenPlayerVfxAddresses);
        if (hiddenPlayerVfxAddresses.Count == 0)
        {
            ClearHiddenPlayerVfx();
            nextHiddenPlayerVfxIndex = 0;
            return;
        }

        if (nextHiddenPlayerVfxIndex >= hiddenPlayerVfxAddresses.Count)
        {
            nextHiddenPlayerVfxIndex = 0;
        }

        var updatesThisFrame = Math.Min(HiddenPlayerVfxUpdatesPerFrame, hiddenPlayerVfxAddresses.Count);
        for (var offset = 0; offset < updatesThisFrame; offset++)
        {
            var index = (nextHiddenPlayerVfxIndex + offset) % hiddenPlayerVfxAddresses.Count;
            var gameObject = (GameObject*)hiddenPlayerVfxAddresses[index];
            var gameObjectId = (ulong)gameObject->GetGameObjectId();
            if (!TryGetScreenVisiblePosition(gameObject, out var position))
            {
                staticVfxController.Hide(gameObjectId);
                continue;
            }

            staticVfxController.ShowOrUpdate(gameObjectId, HiddenPlayerVfxPath, position, gameObject->Rotation);
        }

        nextHiddenPlayerVfxIndex = (nextHiddenPlayerVfxIndex + updatesThisFrame) % hiddenPlayerVfxAddresses.Count;
        PruneHiddenPlayerVfxIfNeeded();
        hiddenPlayerVfxAddresses.Clear();
    }

    private void PruneHiddenPlayerVfxIfNeeded()
    {
        var now = Environment.TickCount64;
        if (now < nextHiddenPlayerVfxPrune)
        {
            return;
        }

        currentHiddenPlayerVfxIds.Clear();
        foreach (var address in hiddenPlayerVfxAddresses)
        {
            var gameObject = (GameObject*)address;
            if (gameObject != null)
            {
                currentHiddenPlayerVfxIds.Add((ulong)gameObject->GetGameObjectId());
            }
        }

        staticVfxController.PruneExcept(currentHiddenPlayerVfxIds);
        currentHiddenPlayerVfxIds.Clear();
        nextHiddenPlayerVfxPrune = now + HiddenPlayerVfxPruneIntervalMs;
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

    private static CullingPerformanceTrace CreateTrace(
        bool isRefresh,
        bool refreshPlayerPreview,
        long start,
        long guardStart,
        long keepPlanTicks,
        long visibilityPlanTicks,
        long reconcileTicks,
        long previewTicks,
        CullingTickPerformanceTrace tick
    ) =>
        new(
            isRefresh,
            refreshPlayerPreview,
            0,
            0,
            0,
            Stopwatch.GetTimestamp() - start,
            Stopwatch.GetTimestamp() - guardStart,
            keepPlanTicks,
            visibilityPlanTicks,
            reconcileTicks,
            previewTicks,
            tick
        );

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
