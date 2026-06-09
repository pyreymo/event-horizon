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

    public bool NeedsDynamicRefresh => IsCullingEnabled() && playerKeepRules.NeedsDynamicRefresh;

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

        playerKeepRules.BeforeUpdate();

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

        foreach (var (address, record) in hiddenObjects)
        {
            if (TryFindObject(manager, address, record, out var gameObject, out _))
            {
                gameObject->RenderFlags &= ~record.AddedFlags;
            }
        }

        Clear();
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

    private bool ShouldHideObject(GameObject* gameObject, int index)
    {
        return gameObject != null
            && IsCullingEnabled()
            && playerState.IsLoaded
            && IsPlayerRelatedSlot(index)
            && !IsLocalPlayerReservedSlot(index)
            && !IsOwnedByLocalPlayer(gameObject)
            && !playerKeepRules.ShouldKeep(gameObject);
    }

    #endregion

    #region Object Helpers

    private static bool IsPlayerRelatedSlot(int index) => index is >= 0 and <= 199;

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
        VisibilityFlags AddedFlags
    )
    {
        public static HiddenObjectRecord From(GameObject* gameObject, VisibilityFlags targetFlags)
        {
            var addedFlags = targetFlags & ~gameObject->RenderFlags;
            return new((ulong)gameObject->GetGameObjectId(), gameObject->EntityId, addedFlags);
        }

        public bool IsSameObject(GameObject* gameObject) =>
            gameObject != null
            && (ulong)gameObject->GetGameObjectId() == GameObjectId
            && gameObject->EntityId == EntityId;
    }

    #endregion
}
