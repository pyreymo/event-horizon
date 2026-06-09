using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.ObjectTable;

internal sealed unsafe class ObjectCuller(
    Configuration configuration,
    IPlayerState playerState,
    IObjectTable objectTable
) : IDisposable
{
    private const VisibilityFlags InvisibleFlag =
        (VisibilityFlags)0x8000 | VisibilityFlags.Nameplate | VisibilityFlags.Model;

    private readonly Configuration configuration = configuration;
    private readonly IPlayerState playerState = playerState;
    private readonly IObjectTable objectTable = objectTable;
    private readonly Dictionary<nint, HiddenObjectRecord> hiddenObjects = [];
    private readonly HashSet<ulong> nearbyKeptPlayers = [];

    public bool NeedsDynamicRefresh => IsCullingEnabled() && configuration.KeepNearbyPlayers;

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
        ClearKeepState();
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
            && !ShouldKeepPlayer(gameObject);
    }

    #endregion

    #region Player Keep Rules

    private bool ShouldKeepPlayer(GameObject* gameObject)
    {
        return IsPlayerObject(gameObject)
            && (
                ShouldKeepFriend(gameObject)
                || ShouldKeepPartyOrAllianceMember(gameObject)
                || ShouldKeepNearbyPlayer(gameObject)
                || ShouldKeepByRace(gameObject)
            );
    }

    private bool ShouldKeepFriend(GameObject* gameObject)
    {
        if (!configuration.KeepFriends)
        {
            return false;
        }

        return ((BattleChara*)gameObject)->IsFriend;
    }

    private bool ShouldKeepPartyOrAllianceMember(GameObject* gameObject)
    {
        if (!configuration.KeepPartyAndAllianceMembers)
        {
            return false;
        }

        var player = (BattleChara*)gameObject;
        return player->IsPartyMember || player->IsAllianceMember;
    }

    private bool ShouldKeepNearbyPlayer(GameObject* gameObject)
    {
        if (!configuration.KeepNearbyPlayers)
        {
            return false;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            return false;
        }

        var player = (BattleChara*)gameObject;
        var playerId = (ulong)((GameObject*)player)->GetGameObjectId();

        var range = Math.Clamp(configuration.KeepNearbyPlayersRange, 1f, 50f);
        var distanceSq = Vector3.DistanceSquared(localPlayer.Position, player->Position);

        if (nearbyKeptPlayers.Contains(playerId))
        {
            if (distanceSq <= range * range)
            {
                return true;
            }

            nearbyKeptPlayers.Remove(playerId);
            return false;
        }

        if (distanceSq > range * range)
        {
            return false;
        }

        nearbyKeptPlayers.Add(playerId);
        return true;
    }

    private bool ShouldKeepByRace(GameObject* gameObject)
    {
        if (!configuration.KeepSelectedRaces)
        {
            return false;
        }

        var player = (BattleChara*)gameObject;
        var customizeData = player->DrawData.CustomizeData;
        return configuration.KeptRaceSex.Contains(
            RaceSexFilter.Pack(customizeData.Race, customizeData.Sex)
        );
    }

    #endregion

    #region Keep State

    private void ClearKeepState()
    {
        nearbyKeptPlayers.Clear();
    }

    #endregion

    #region Object Helpers

    private static bool IsPlayerObject(GameObject* gameObject) =>
        gameObject->ObjectKind == ObjectKind.Pc;

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
