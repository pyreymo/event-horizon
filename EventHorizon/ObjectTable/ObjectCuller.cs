using System;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.ObjectTable;

internal sealed unsafe class ObjectCuller : IDisposable
{
    private const VisibilityFlags PluginCustomProbe = (VisibilityFlags)0x1000;
    private const VisibilityFlags InvisibleFlag =
        PluginCustomProbe | VisibilityFlags.Nameplate | VisibilityFlags.Model;

    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly PlayerKeepRules playerKeepRules;
    private readonly HiddenObjectTracker hiddenObjectTracker;
    private readonly ObjectFadeController fadeController;

    public ObjectCuller(
        Configuration configuration,
        IPlayerState playerState,
        ICondition condition,
        IObjectTable objectTable,
        ITargetManager targetManager
    )
    {
        this.configuration = configuration;
        this.playerState = playerState;
        this.condition = condition;
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

        var visibleOtherPlayers = 0;

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject == null)
            {
                continue;
            }

            if (gameObject->ObjectKind != ObjectKind.Pc)
            {
                continue;
            }

            var shouldHide = ShouldHidePlayerSlotObject(gameObject, index, ref visibleOtherPlayers);

            if (UpdateFade(gameObject, shouldHide))
            {
                continue;
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

        PruneMissingHiddenObjects(manager);
        PruneMissingFades(manager);
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
    }

    public void ClearRuleState()
    {
        playerKeepRules.Clear();
    }

    public void RecordChatMessage(IChatMessage message)
    {
        playerKeepRules.RecordChatMessage(message);
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

    private bool UpdateFade(GameObject* gameObject, bool shouldHide)
    {
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
        playerKeepRules.Clear();
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
            && ObjectTableStats.CountPlayerObjects(manager)
                < configuration.DisableCullingPlayerCountThreshold;
    }

    private bool ShouldSuspendCullingInDuty()
    {
        return configuration.DisableInDuty
            && (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56]);
    }

    private bool ShouldHidePlayerSlotObject(
        GameObject* gameObject,
        int index,
        ref int visibleOtherPlayers
    )
    {
        if (!IsPlayerRelatedEvenSlot(index) || IsLocalPlayerReservedSlot(index))
        {
            return false;
        }

        return !playerKeepRules.ShouldKeep(gameObject)
            || ShouldHideByVisiblePlayerLimit(ref visibleOtherPlayers);
    }

    private bool ShouldHideNonPlayerSlotObject(
        GameObjectManager* manager,
        GameObject* gameObject,
        int index
    )
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

    private bool ShouldHideByVisiblePlayerLimit(ref int visibleOtherPlayers)
    {
        if (!configuration.LimitVisiblePlayerCount)
        {
            return false;
        }

        var visiblePlayerLimit = Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 100);
        if (visibleOtherPlayers < visiblePlayerLimit)
        {
            visibleOtherPlayers++;
            return false;
        }

        return true;
    }

    #endregion

    #region Object Helpers

    private static bool IsPlayerRelatedSlot(int index) => index is >= 0 and <= 199;

    private static bool IsPlayerRelatedEvenSlot(int index) =>
        IsPlayerRelatedSlot(index) && index % 2 == 0;

    private static bool IsPlayerRelatedOddSlot(int index) =>
        IsPlayerRelatedSlot(index) && index % 2 == 1;

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
            if (
                owner != null
                && owner->ObjectKind == ObjectKind.Pc
                && owner->EntityId == gameObject->OwnerId
            )
            {
                return owner;
            }
        }

        return null;
    }

    private bool IsHiddenByThisPlugin(GameObject* gameObject)
    {
        return hiddenObjectTracker.IsHidden(gameObject);
    }

    public int GetHiddenPlayerCount() => hiddenObjectTracker.HiddenPlayerCount;

    public bool NeedsDynamicRefresh()
    {
        return IsCullingEnabled() && !ShouldSuspendCullingInDuty() && playerState.IsLoaded;
    }

    public bool HasActiveFades => fadeController.HasActiveFades;

    #endregion
}
