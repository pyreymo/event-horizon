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
    private const int MaxHiddenPlayerVfxCreatesPerFrame = 8;
    private const int MaxPlayerRelatedObjectIndex = 199;
    private const VisibilityFlags PluginCustomProbe = (VisibilityFlags)0x1000;
    private const VisibilityFlags InvisibleFlag = PluginCustomProbe | VisibilityFlags.Nameplate | VisibilityFlags.Model;
    private const string HiddenPlayerVfxPath = StaticVfxResourceRedirector.HiddenPlayerGroundMarkerPath;

    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly StaticVfxController staticVfxController;
    private readonly PlayerKeepRules playerKeepRules;
    private readonly EventNpcVisibilityRule eventNpcVisibilityRule = new();
    private readonly PlayerKeepPlan playerKeepPlan = new();
    private readonly List<PlayerKeepCandidate> playerKeepCandidates = [];
    private readonly HiddenObjectTracker hiddenObjectTracker;
    private readonly PlayerVisibilityPipeline playerVisibilityPipeline;
    private readonly ShowTransitionBudget showTransitionBudget = new();
    private readonly PlayerAdmissionGate playerAdmissionGate = new();
    private readonly PlayerTopologyDirtySignal playerTopologyDirtySignal = new();
    private readonly PlayerVisibilityAppliedState appliedVisibilityState = new();
    private readonly PlayerObjectIdentity?[] playerAdmissionSlotIdentities = new PlayerObjectIdentity?[
        PlayerAdmissionGate.LastPlayerSlot + 1
    ];
    private readonly Dictionary<ulong, string> playerPreviewNames = [];
    private readonly List<nint> hiddenPlayerVfxAddresses = [];
    private readonly List<HiddenPlayerVfxCandidate> hiddenPlayerVfxCandidates = [];
    private readonly HashSet<ulong> liveHiddenPlayerVfxIds = [];
    private readonly HashSet<uint> hiddenPlayerOwnerEntityIds = [];
    private readonly HashSet<uint> oddSlotPlayerOwnerIds = [];
    private PlayerKeepBudgetStats keepBudgetStats;
    private PlayerPreviewSnapshot playerPreviewSnapshot = PlayerPreviewSnapshot.Empty;
    private uint? previewSelectedPlayerEntityId;
    private long previewSelectionExpiresAt;
    private int nextPlayerVisibilityGeneration;
    private PlayerVisibilityReconciliation? latestPlayerVisibilityReconciliation;
    private readonly CullingRuntimeModeTransition runtimeModeTransition = new();

    public CullingPerformanceTrace LastUpdateTrace { get; private set; }
    public CullingPerformanceTrace LastTickTrace { get; private set; }

    public ObjectCuller(
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
        this.configuration = configuration;
        this.playerState = playerState;
        this.condition = condition;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.staticVfxController = staticVfxController;
        playerVisibilityPipeline = new(exception => log.Error(exception, "Player visibility selection failed; using legacy fallback."));
        playerKeepRules = new(configuration, objectTable, targetManager);
        hiddenObjectTracker = new();
    }

    #region Lifecycle

    public CullingRuntimeSynchronization SynchronizeRuntimeMode(GameObjectManager* manager)
    {
        var nextMode = DetermineRuntimeMode(manager);
        var transition = runtimeModeTransition.Synchronize(nextMode);
        if (!transition.Changed)
        {
            return new(nextMode, RequiresRefresh: false);
        }

        if (transition.EnterInactive)
        {
            EnterInactiveMode(manager, transition.ClearLongTermRules);
        }
        else if (transition.RebuildActive)
        {
            ClearPublishedPlayerVisibilityState();
        }

        return new(nextMode, RequiresRefresh: transition.RebuildActive);
    }

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
            playerVisibilityPipeline.Reset();
            RestoreHiddenObjects(manager);
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
            playerVisibilityPipeline.Reset();
            RestoreHiddenObjects(manager);
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
            playerVisibilityPipeline.Reset();
            RestoreHiddenObjects(manager);
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

        eventNpcVisibilityRule.Refresh(manager, configuration.HideUnattachedEventNpcs);

        var guardTicks = Stopwatch.GetTimestamp() - phaseStart;
        phaseStart = Stopwatch.GetTimestamp();
        playerKeepPlan.Update(configuration, GetPlayerKeepCandidates(manager));
        var keepPlanTicks = Stopwatch.GetTimestamp() - phaseStart;
        phaseStart = Stopwatch.GetTimestamp();
        var playerVisibilityPlan = playerVisibilityPipeline.BuildPlan(
            ++nextPlayerVisibilityGeneration,
            manager,
            playerKeepPlan,
            GetActivePreviewSelectedPlayerEntityId()
        );
        var legacyTargetSet = playerVisibilityPipeline.BuildLegacyTarget(playerVisibilityPlan);
        var visibilityPlanTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        var frameState = playerVisibilityPipeline.BuildFrame(
            playerVisibilityPlan,
            legacyTargetSet,
            configuration.LimitVisiblePlayerCount,
            configuration.VisiblePlayerCountLimit,
            objectTable.LocalPlayer?.Position,
            hiddenObjectTracker
        );
        var reconcileTicks = Stopwatch.GetTimestamp() - phaseStart;
        keepBudgetStats = frameState.BudgetStats;
        appliedVisibilityState.Publish(frameState);
        latestPlayerVisibilityReconciliation = frameState.Reconciliation;
        playerVisibilityPipeline.Commit(frameState);
        var appliedTarget = frameState.ActiveTarget;
        var activeTargetSet = frameState.ActiveTarget;
        var playerVisibilityReconciliation = frameState.Reconciliation;

        long previewBeginTicks = 0;
        long previewAddTicks = 0;
        long previewBuildTicks = 0;
        var previewBuilder = default(PlayerPreviewBuilder);
        if (refreshPlayerPreview)
        {
            phaseStart = Stopwatch.GetTimestamp();
            previewBuilder = PlayerPreviewBuilder.Begin(manager, configuration);
            previewBeginTicks = Stopwatch.GetTimestamp() - phaseStart;
        }

        if (previewBuilder != null)
        {
            phaseStart = Stopwatch.GetTimestamp();
            AddPlayerPreviewEntries(manager, appliedTarget, previewBuilder);
            previewAddTicks = Stopwatch.GetTimestamp() - phaseStart;
        }
        var previewTicks = previewBeginTicks + previewAddTicks;

        var tickTrace = default(CullingTickPerformanceTrace);
        if (previewBuilder != null)
        {
            phaseStart = Stopwatch.GetTimestamp();
            playerPreviewSnapshot = previewBuilder.Build();
            previewBuildTicks = Stopwatch.GetTimestamp() - phaseStart;
            previewTicks += previewBuildTicks;
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
            Tick: tickTrace,
            Preview: new CullingPreviewPerformanceTrace(previewBuilder?.Count ?? 0, previewBeginTicks, previewAddTicks, previewBuildTicks),
            PlayerVisibilityClasses: activeTargetSet.ClassificationCounts,
            Selection: frameState.SelectionTrace
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

        if (runtimeModeTransition.Current != CullingRuntimeMode.Active)
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
            Tick: tickTrace,
            PlayerVisibilityClasses: appliedVisibilityState.ActiveTarget?.ClassificationCounts ?? default
        );
    }

    public void ApplyPlayerAdmissionGate(GameObjectManager* manager)
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

                Hide(gameObject, change.Slot);
            }
        );
        playerTopologyDirtySignal.MarkFrom(result);
    }

    public void Reset(GameObjectManager* manager)
    {
        if (manager == null)
        {
            Clear();
            return;
        }

        RestoreHiddenObjects(manager);
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
        playerVisibilityPipeline.Reset();
    }

    public void RecordChatMessage(IChatMessage message)
    {
        playerKeepRules.RecordChatMessage(message);
    }

    public void RefreshPlayerPreview(GameObjectManager* manager)
    {
        if (manager == null || !IsCullingEnabled() || !playerState.IsLoaded || appliedVisibilityState.ActiveTarget == null)
        {
            return;
        }

        var previewBuilder = PlayerPreviewBuilder.Begin(manager, configuration);
        AddPlayerPreviewEntries(manager, appliedVisibilityState.ActiveTarget, previewBuilder);
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
            ApplyPlayerVisibilityAction(manager, action);
        }
    }

    private void ApplyPlayerVisibilityAction(GameObjectManager* manager, PlayerVisibilityAction action)
    {
        switch (action.Kind)
        {
            case PlayerVisibilityActionKind.Show:
                ApplyShowAction(manager, action.Target);
                break;
            case PlayerVisibilityActionKind.Hide:
                ApplyHideAction(manager, action.Target);
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
        var hiddenVfxTrace = UpdateHiddenPlayerVfx(manager);
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
                hiddenVfxTicks,
                hiddenVfxTrace
            ),
            PlayerVisibilityClasses: appliedVisibilityState.ActiveTarget?.ClassificationCounts ?? default
        );
    }

    private void ApplyNonPlayerVisibility(GameObjectManager* manager)
    {
        CollectHiddenPlayerOwnerEntityIds(manager);
        CollectOddSlotPlayerOwnerIds(manager);

        var maxIndex = Math.Min(EventNpcVisibilityRule.LastSlot, manager->Objects.IndexSorted.Length - 1);
        for (var index = 0; index <= maxIndex; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject == null || gameObject->ObjectKind == ObjectKind.Pc)
            {
                continue;
            }

            if (ShouldHideNonPlayerSlotObject(gameObject, index))
            {
                Hide(gameObject, index);
            }
            else
            {
                RestoreIfHidden(gameObject);
            }
        }

        hiddenPlayerOwnerEntityIds.Clear();
        oddSlotPlayerOwnerIds.Clear();
    }

    private void CollectHiddenPlayerOwnerEntityIds(GameObjectManager* manager)
    {
        hiddenPlayerOwnerEntityIds.Clear();
        var maxIndex = Math.Min(MaxPlayerRelatedObjectIndex, manager->Objects.IndexSorted.Length - 1);
        for (var index = 0; index <= maxIndex; index++)
        {
            if (!IsPlayerRelatedEvenSlot(index))
            {
                continue;
            }

            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject != null && gameObject->ObjectKind == ObjectKind.Pc && hiddenObjectTracker.IsHidden(gameObject))
            {
                hiddenPlayerOwnerEntityIds.Add(gameObject->EntityId);
            }
        }
    }

    private void CollectOddSlotPlayerOwnerIds(GameObjectManager* manager)
    {
        oddSlotPlayerOwnerIds.Clear();
        if (!configuration.HideOtherPlayerBattlePets)
        {
            return;
        }

        var maxIndex = Math.Min(MaxPlayerRelatedObjectIndex, manager->Objects.IndexSorted.Length - 1);
        for (var index = 0; index <= maxIndex; index++)
        {
            if (!IsPlayerRelatedOddSlot(index) || IsLocalPlayerReservedSlot(index))
            {
                continue;
            }

            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject == null || gameObject->ObjectKind != ObjectKind.Pc)
            {
                continue;
            }

            oddSlotPlayerOwnerIds.Add(gameObject->EntityId);

            var gameObjectId = (ulong)gameObject->GetGameObjectId();
            if (gameObjectId <= uint.MaxValue)
            {
                oddSlotPlayerOwnerIds.Add((uint)gameObjectId);
            }
        }
    }

    private void ApplyShowAction(GameObjectManager* manager, PlayerVisibilityTarget target)
    {
        var gameObject = FindPlayerObject(manager, target.Identity, target.ObjectIndex);
        if (gameObject == null)
        {
            return;
        }

        var wasHidden = hiddenObjectTracker.IsHidden(gameObject);
        if (wasHidden && !showTransitionBudget.CanStartShow())
        {
            return;
        }

        RestoreIfHidden(gameObject);
        if (wasHidden && !hiddenObjectTracker.IsHidden(gameObject))
        {
            showTransitionBudget.ConsumeShow();
        }
    }

    private void ApplyHideAction(GameObjectManager* manager, PlayerVisibilityTarget target)
    {
        var gameObject = FindPlayerObject(manager, target.Identity, target.ObjectIndex);
        if (gameObject != null)
        {
            Hide(gameObject, target.ObjectIndex);
        }
    }

    private void ApplySwapAction(GameObjectManager* manager, PlayerVisibilityAction action)
    {
        if (!action.PairedTarget.HasValue)
        {
            ApplyShowAction(manager, action.Target);
            return;
        }

        var incoming = FindPlayerObject(manager, action.Target.Identity, action.Target.ObjectIndex);
        if (incoming == null)
        {
            return;
        }

        var incomingWasHidden = hiddenObjectTracker.IsHidden(incoming);
        if (incomingWasHidden && !showTransitionBudget.CanStartShow())
        {
            return;
        }

        var outgoingTarget = action.PairedTarget.Value;
        var outgoing = FindPlayerObject(manager, outgoingTarget.Identity, outgoingTarget.ObjectIndex);
        if (outgoing != null)
        {
            Hide(outgoing, outgoingTarget.ObjectIndex);
        }

        RestoreIfHidden(incoming);
        if (incomingWasHidden && !hiddenObjectTracker.IsHidden(incoming))
        {
            showTransitionBudget.ConsumeShow();
        }
    }

    private void AddPlayerPreviewEntries(
        GameObjectManager* manager,
        PlayerVisibilityTargetSet playerVisibilityTargetSet,
        PlayerPreviewBuilder previewBuilder
    )
    {
        foreach (var target in playerVisibilityTargetSet.Targets)
        {
            if (IsLocalPlayerReservedSlot(target.ObjectIndex) || !IsPlayerRelatedEvenSlot(target.ObjectIndex))
            {
                continue;
            }

            var gameObject = FindPlayerObject(manager, target.Identity, target.ObjectIndex);
            if (gameObject != null)
            {
                previewBuilder.Add(
                    gameObject,
                    target.ObjectIndex,
                    GetCachedPlayerPreviewName(gameObject),
                    target.Decision,
                    !target.DesiredVisible,
                    target.CutByBudget
                );
            }
        }
    }

    private string GetCachedPlayerPreviewName(GameObject* gameObject)
    {
        var gameObjectId = (ulong)gameObject->GetGameObjectId();
        if (gameObjectId != 0 && playerPreviewNames.TryGetValue(gameObjectId, out var name))
        {
            return name;
        }

        name = PlayerPreviewBuilder.GetObjectName(gameObject);
        if (gameObjectId != 0 && !IsFallbackPreviewName(name))
        {
            playerPreviewNames[gameObjectId] = name;
        }

        return name;
    }

    private static bool IsFallbackPreviewName(string name)
    {
        return name.Length == 9 && name[0] == '#';
    }

    private void PruneMissingHiddenObjects(GameObjectManager* manager)
    {
        hiddenObjectTracker.PruneMissing(manager);
    }

    private void Clear()
    {
        hiddenObjectTracker.Clear();
        showTransitionBudget.Reset();
        ClearHiddenPlayerVfx();
        playerKeepRules.Clear();
        playerPreviewNames.Clear();
        keepBudgetStats = default;
        playerPreviewSnapshot = PlayerPreviewSnapshot.Empty;
        previewSelectedPlayerEntityId = null;
        previewSelectionExpiresAt = 0;
        playerAdmissionGate.RequestReset();
        playerTopologyDirtySignal.Clear();
        appliedVisibilityState.Clear();
        playerVisibilityPipeline.Reset();
        latestPlayerVisibilityReconciliation = null;
        eventNpcVisibilityRule.Clear();
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

    private bool ShouldHideNonPlayerSlotObject(GameObject* gameObject, int index)
    {
        if (IsLocalPlayerReservedSlot(index))
        {
            return false;
        }

        if (index is >= EventNpcVisibilityRule.FirstSlot and <= EventNpcVisibilityRule.LastSlot)
        {
            return eventNpcVisibilityRule.ShouldHide(gameObject, index);
        }

        if (IsPlayerRelatedEvenSlot(index))
        {
            return gameObject->ObjectKind == ObjectKind.BattleNpc
                && gameObject->OwnerId != 0
                && hiddenPlayerOwnerEntityIds.Contains(gameObject->OwnerId);
        }

        if (IsPlayerRelatedOddSlot(index))
        {
            if (
                configuration.HideOtherPlayerBattlePets
                && gameObject->ObjectKind == ObjectKind.BattleNpc
                && gameObject->OwnerId != 0
                && oddSlotPlayerOwnerIds.Contains(gameObject->OwnerId)
            )
            {
                return true;
            }

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
                keepDecision = keepDecision.WithViewport(IsScreenVisibleObject(gameObject));
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

    private CullingHiddenVfxPerformanceTrace UpdateHiddenPlayerVfx(GameObjectManager* manager)
    {
        if (!configuration.EnableHiddenPlayerGroundMarker || manager == null)
        {
            var clearStart = Stopwatch.GetTimestamp();
            ClearHiddenPlayerVfx();
            return new CullingHiddenVfxPerformanceTrace(
                0,
                0,
                staticVfxController.ActiveCount,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                Stopwatch.GetTimestamp() - clearStart
            );
        }

        var phaseStart = Stopwatch.GetTimestamp();
        hiddenPlayerVfxAddresses.Clear();
        hiddenPlayerVfxCandidates.Clear();
        liveHiddenPlayerVfxIds.Clear();
        hiddenObjectTracker.CollectHiddenPlayerAddresses(manager, hiddenPlayerVfxAddresses);
        var collectTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        foreach (var address in hiddenPlayerVfxAddresses)
        {
            var gameObject = (GameObject*)address;
            if (!TryGetObjectPosition(gameObject, out var position))
            {
                continue;
            }

            var gameObjectId = (ulong)gameObject->GetGameObjectId();
            liveHiddenPlayerVfxIds.Add(gameObjectId);

            var isActive = staticVfxController.IsActive(gameObjectId, HiddenPlayerVfxPath);
            if (!isActive && !IsScreenVisiblePosition(position))
            {
                continue;
            }

            hiddenPlayerVfxCandidates.Add(new(gameObjectId, position, gameObject->Rotation, isActive));
        }
        var projectTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        var showCreatedCount = 0;
        var showUpdatedCount = 0;
        var showSkippedCount = 0;
        var showRemovedCount = 0;
        var showDeferredCount = 0;
        var createAttempts = 0;
        foreach (var candidate in hiddenPlayerVfxCandidates)
        {
            if (!candidate.IsActive && createAttempts >= MaxHiddenPlayerVfxCreatesPerFrame)
            {
                showDeferredCount++;
                continue;
            }

            if (!candidate.IsActive)
            {
                createAttempts++;
            }

            var result = staticVfxController.ShowOrUpdate(
                candidate.GameObjectId,
                HiddenPlayerVfxPath,
                candidate.Position,
                candidate.Rotation
            );
            if (result.Created)
            {
                showCreatedCount++;
            }

            if (result.Updated)
            {
                showUpdatedCount++;
            }

            if (result.Skipped)
            {
                showSkippedCount++;
            }

            if (result.Removed)
            {
                showRemovedCount++;
            }
        }
        var showTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        staticVfxController.PruneExcept(liveHiddenPlayerVfxIds);
        var pruneTicks = Stopwatch.GetTimestamp() - phaseStart;
        var activeCount = staticVfxController.ActiveCount;
        var hiddenCount = hiddenPlayerVfxAddresses.Count;
        var visibleCount = hiddenPlayerVfxCandidates.Count;
        hiddenPlayerVfxAddresses.Clear();
        hiddenPlayerVfxCandidates.Clear();
        liveHiddenPlayerVfxIds.Clear();
        return new CullingHiddenVfxPerformanceTrace(
            hiddenCount,
            visibleCount,
            activeCount,
            showCreatedCount,
            showUpdatedCount,
            showSkippedCount,
            showRemovedCount,
            showDeferredCount,
            collectTicks,
            projectTicks,
            showTicks,
            pruneTicks,
            0
        );
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

    private bool IsScreenVisiblePosition(Vector3 position)
    {
        return gameGui.WorldToScreen(position, out _, out var inView) && inView;
    }

    private bool IsScreenVisibleObject(GameObject* gameObject)
    {
        return TryGetObjectPosition(gameObject, out var position) && IsScreenVisiblePosition(position);
    }

    private void ClearHiddenPlayerVfx()
    {
        staticVfxController.Clear();
    }

    private readonly record struct HiddenPlayerVfxCandidate(ulong GameObjectId, Vector3 Position, float Rotation, bool IsActive);

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

    public bool ConsumePlayerTopologyDirty() => playerTopologyDirtySignal.Consume();

    public PlayerAdmissionDiagnostics GetPlayerAdmissionDiagnostics() => playerAdmissionGate.GetDiagnostics();

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

    private void EnterInactiveMode(GameObjectManager* manager, bool clearLongTermRuleState)
    {
        if (manager != null)
        {
            RestoreHiddenObjects(manager);
        }
        else
        {
            hiddenObjectTracker.Clear();
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
        playerVisibilityPipeline.Reset();
        showTransitionBudget.Reset();
        keepBudgetStats = default;
        playerPreviewSnapshot = PlayerPreviewSnapshot.Empty;
        ClearHiddenPlayerVfx();
    }

    #endregion
}
