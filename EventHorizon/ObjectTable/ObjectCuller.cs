using System;
using System.Collections.Generic;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.ObjectTable;

internal sealed unsafe class ObjectCuller(
    Configuration configuration,
    IPlayerState playerState,
    IObjectTable objectTable,
    ITargetManager targetManager
) : IDisposable
{
    private const VisibilityFlags PluginCustomProbe = (VisibilityFlags)0x1000;
    private const VisibilityFlags InvisibleFlag =
        PluginCustomProbe | VisibilityFlags.Nameplate | VisibilityFlags.Model;

    private readonly Configuration configuration = configuration;
    private readonly IPlayerState playerState = playerState;
    private readonly PlayerKeepRules playerKeepRules = new(
        configuration,
        objectTable,
        targetManager
    );
    private readonly Dictionary<nint, HiddenObjectRecord> hiddenObjects = [];

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

        if (ShouldSuspendCulling(manager))
        {
            RestoreHiddenObjects(manager);
            return;
        }

        playerKeepRules.BeforeUpdate();
        if (!playerState.IsLoaded)
        {
            RestoreHiddenObjects(manager);
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

            if (ShouldHidePlayerSlotObject(gameObject, index, ref visibleOtherPlayers))
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
        foreach (var (address, record) in hiddenObjects)
        {
            var gameObject = FindObject(manager, address, record);
            if (gameObject != null)
            {
                gameObject->RenderFlags &= ~record.AddedFlags;
            }
        }

        hiddenObjects.Clear();
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
        if (gameObject == null)
        {
            return;
        }

        var address = (nint)gameObject;
        if (address == nint.Zero)
        {
            return;
        }

        if (!hiddenObjects.TryGetValue(address, out var record) || !record.IsSameObject(gameObject))
        {
            hiddenObjects[address] = HiddenObjectRecord.From(gameObject, InvisibleFlag);
        }

        gameObject->RenderFlags |= InvisibleFlag;
    }

    private void RestoreIfHidden(GameObject* gameObject)
    {
        if (gameObject == null)
        {
            return;
        }

        var address = (nint)gameObject;
        if (!hiddenObjects.TryGetValue(address, out var record))
        {
            return;
        }

        hiddenObjects.Remove(address);
        if (record.IsSameObject(gameObject))
        {
            gameObject->RenderFlags &= ~record.AddedFlags;
        }
    }

    private void PruneMissingHiddenObjects(GameObjectManager* manager)
    {
        var staleAddresses = new List<nint>();

        foreach (var (address, record) in hiddenObjects)
        {
            if (FindObject(manager, address, record) == null)
            {
                staleAddresses.Add(address);
            }
        }

        foreach (var address in staleAddresses)
        {
            hiddenObjects.Remove(address);
        }
    }

    private void Clear()
    {
        hiddenObjects.Clear();
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
            return ShouldHideOddSlotObject(manager, gameObject, index);
        }

        return false;
    }

    private bool ShouldHideOddSlotObject(
        GameObjectManager* manager,
        GameObject* gameObject,
        int index
    )
    {
        var owner = manager->Objects.IndexSorted[index - 1].Value;
        if (owner == null || gameObject->OwnerId != owner->EntityId)
        {
            return false;
        }

        if (IsHiddenByThisPlugin(owner))
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

    private bool ShouldHideByVisiblePlayerLimit(ref int visibleOtherPlayers)
    {
        if (!configuration.LimitVisiblePlayerCount)
        {
            return false;
        }

        var visiblePlayerLimit = Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 200);
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
        return gameObject != null
            && hiddenObjects.TryGetValue((nint)gameObject, out var record)
            && record.IsSameObject(gameObject);
    }

    public int GetHiddenPlayerCount()
    {
        var count = 0;
        foreach (var record in hiddenObjects.Values)
        {
            if (record.ObjectKind == ObjectKind.Pc)
            {
                count++;
            }
        }

        return count;
    }

    public bool NeedsDynamicRefresh()
    {
        return IsCullingEnabled() && playerKeepRules.NeedsDynamicRefresh;
    }

    private static GameObject* FindObject(
        GameObjectManager* manager,
        nint address,
        HiddenObjectRecord record
    )
    {
        if (manager == null || address == nint.Zero)
        {
            return null;
        }

        for (var i = 0; i < manager->Objects.IndexSorted.Length; i++)
        {
            ref var entry = ref manager->Objects.IndexSorted[i];
            if ((nint)entry.Value == address && record.IsSameObject(entry.Value))
            {
                return entry.Value;
            }
        }

        return null;
    }

    #endregion

    #region Records

    private readonly record struct HiddenObjectRecord(
        ulong GameObjectId,
        uint EntityId,
        ObjectKind ObjectKind,
        VisibilityFlags AddedFlags
    )
    {
        public static HiddenObjectRecord From(GameObject* gameObject, VisibilityFlags targetFlags)
        {
            var addedFlags = targetFlags & ~gameObject->RenderFlags;
            return new(
                (ulong)gameObject->GetGameObjectId(),
                gameObject->EntityId,
                gameObject->ObjectKind,
                addedFlags
            );
        }

        public bool IsSameObject(GameObject* gameObject) =>
            gameObject != null
            && (ulong)gameObject->GetGameObjectId() == GameObjectId
            && gameObject->EntityId == EntityId;
    }

    #endregion
}
