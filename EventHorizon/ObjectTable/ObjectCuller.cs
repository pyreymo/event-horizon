using System;
using System.Collections.Generic;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using BattleNpcSubKind = Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace EventHorizon.ObjectTable;

internal sealed unsafe class ObjectCuller(
    Configuration configuration,
    IPlayerState playerState,
    IObjectTable objectTable,
    ITargetManager targetManager
) : IDisposable
{
    private const VisibilityFlags InvisibleFlag =
        (VisibilityFlags)0x8000 | VisibilityFlags.Nameplate | VisibilityFlags.Model;

    private readonly Configuration configuration = configuration;
    private readonly IPlayerState playerState = playerState;
    private readonly PlayerKeepRules playerKeepRules = new(
        configuration,
        objectTable,
        targetManager
    );
    private readonly Dictionary<nint, HiddenObjectRecord> hiddenObjects = [];
    private int visibleOtherPlayersThisScan;

    public bool NeedsDynamicRefresh => IsCullingEnabled() && playerKeepRules.NeedsDynamicRefresh;
    public int HiddenPlayerCount
    {
        get
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

        if (ShouldSuspendCulling(manager))
        {
            RestoreHiddenObjects(manager);
            return;
        }

        playerKeepRules.BeforeUpdate();
        visibleOtherPlayersThisScan = 0;

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (ShouldHideObject(gameObject, index))
            {
                Hide(gameObject);
            }
            else
            {
                RestoreIfHidden(gameObject);
            }
        }

        RestoreNoLongerCulled(manager);
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
            if (TryFindObject(manager, address, record, out var gameObject, out _))
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

    public void RecordChatMessage(IHandleableChatMessage message)
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

    private void RestoreNoLongerCulled(GameObjectManager* manager)
    {
        foreach (var address in new List<nint>(hiddenObjects.Keys))
        {
            var record = hiddenObjects[address];
            if (
                TryFindObject(manager, address, record, out var gameObject, out var index)
                && ShouldHideObject(gameObject, index)
            )
            {
                continue;
            }

            if (gameObject != null)
            {
                gameObject->RenderFlags &= ~record.AddedFlags;
            }

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

    private bool ShouldHideObject(GameObject* gameObject, int index)
    {
        return gameObject != null
            && IsCullingEnabled()
            && playerState.IsLoaded
            && IsCullableOtherPlayerRelatedObject(gameObject, index)
            && !IsLocalPlayerReservedSlot(index)
            && !IsOwnedByLocalPlayer(gameObject)
            && (
                !playerKeepRules.ShouldKeep(gameObject)
                || ShouldHideByVisiblePlayerLimit(gameObject, index)
            );
    }

    private bool ShouldHideByVisiblePlayerLimit(GameObject* gameObject, int index)
    {
        if (!configuration.LimitVisiblePlayerCount || !IsOtherPlayerObject(gameObject, index))
        {
            return false;
        }

        var visiblePlayerLimit = Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 200);
        if (visibleOtherPlayersThisScan < visiblePlayerLimit)
        {
            visibleOtherPlayersThisScan++;
            return false;
        }

        return true;
    }

    #endregion

    #region Object Helpers

    private static bool IsCullableOtherPlayerRelatedObject(GameObject* gameObject, int index)
    {
        return IsOtherPlayerObject(gameObject, index)
            || IsOtherPlayerCompanionOrOrnament(gameObject, index)
            || IsOtherPlayerBattlePet(gameObject, index);
    }

    private static bool IsOtherPlayerObject(GameObject* gameObject, int index) =>
        IsPlayerRelatedEvenSlot(index) && gameObject->ObjectKind == ObjectKind.Pc;

    private static bool IsOtherPlayerCompanionOrOrnament(GameObject* gameObject, int index) =>
        IsPlayerRelatedOddSlot(index)
        && gameObject->ObjectKind is ObjectKind.Companion or ObjectKind.Ornament;

    private static bool IsOtherPlayerBattlePet(GameObject* gameObject, int index) =>
        IsPlayerRelatedEvenSlot(index)
        && gameObject->ObjectKind == ObjectKind.BattleNpc
        && (BattleNpcSubKind)gameObject->SubKind == BattleNpcSubKind.Pet;

    private static bool IsPlayerRelatedEvenSlot(int index) =>
        index is >= 0 and <= 199 && index % 2 == 0;

    private static bool IsPlayerRelatedOddSlot(int index) =>
        index is >= 0 and <= 199 && index % 2 == 1;

    private static bool IsLocalPlayerReservedSlot(int index) => index is 0 or 1;

    private bool IsOwnedByLocalPlayer(GameObject* gameObject) =>
        gameObject->OwnerId == playerState.EntityId;

    private static bool TryFindObject(
        GameObjectManager* manager,
        nint address,
        HiddenObjectRecord record,
        out GameObject* gameObject,
        out int index
    )
    {
        gameObject = null;
        index = -1;
        if (manager == null || address == nint.Zero)
        {
            return false;
        }

        for (var i = 0; i < manager->Objects.IndexSorted.Length; i++)
        {
            ref var entry = ref manager->Objects.IndexSorted[i];
            if ((nint)entry.Value == address && record.IsSameObject(entry.Value))
            {
                gameObject = entry.Value;
                index = i;
                return true;
            }
        }

        return false;
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
