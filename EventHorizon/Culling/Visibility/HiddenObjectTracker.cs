using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling.Visibility;

internal sealed unsafe class HiddenObjectTracker
{
    internal delegate PlayerObjectIdentity IdentityReader(GameObject* gameObject);

    private readonly Dictionary<nint, HiddenObjectRecord> hiddenObjects = [];
    private readonly List<nint> staleAddresses = [];
    private readonly IdentityReader readIdentity;

    public HiddenObjectTracker()
        : this(PlayerObjectIdentity.From) { }

    internal HiddenObjectTracker(IdentityReader readIdentity)
    {
        this.readIdentity = readIdentity;
    }

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

    public void Hide(GameObject* gameObject, VisibilityFlags targetFlags, int objectIndex)
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

        var identity = readIdentity(gameObject);
        if (!hiddenObjects.TryGetValue(address, out var record) || !record.IsSameObject(identity))
        {
            hiddenObjects[address] = HiddenObjectRecord.From(gameObject, identity, targetFlags, objectIndex);
        }
        else
        {
            hiddenObjects[address] = record with { ObjectIndex = objectIndex };
        }

        gameObject->RenderFlags |= targetFlags;
    }

    public void RestoreIfHidden(GameObject* gameObject)
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
        if (record.IsSameObject(readIdentity(gameObject)))
        {
            gameObject->RenderFlags &= ~record.AddedFlags;
        }
    }

    public void RestoreAll(GameObjectManager* manager)
    {
        if (hiddenObjects.Count == 0)
        {
            return;
        }

        if (manager != null)
        {
            for (var i = 0; i < manager->Objects.IndexSorted.Length; i++)
            {
                var gameObject = manager->Objects.IndexSorted[i].Value;
                if (TryGetLiveRecord(gameObject, out var record))
                {
                    gameObject->RenderFlags &= ~record.AddedFlags;
                }
            }
        }

        hiddenObjects.Clear();
    }

    public void PruneMissing(GameObjectManager* manager)
    {
        if (hiddenObjects.Count == 0)
        {
            return;
        }

        staleAddresses.Clear();

        foreach (var (address, record) in hiddenObjects)
        {
            if (!record.IsLiveAtRecordedIndex(manager, address, readIdentity))
            {
                var movedIndex = FindLiveObjectIndexByAddress(manager, address, record, readIdentity);
                if (movedIndex >= 0)
                {
                    var movedObject = manager->Objects.IndexSorted[movedIndex].Value;
                    movedObject->RenderFlags |= record.AddedFlags;
                    hiddenObjects[address] = record with { ObjectIndex = movedIndex };
                    continue;
                }

                staleAddresses.Add(address);
            }
        }

        foreach (var address in staleAddresses)
        {
            hiddenObjects.Remove(address);
        }

        staleAddresses.Clear();
    }

    public void Clear()
    {
        hiddenObjects.Clear();
    }

    public bool IsHidden(GameObject* gameObject)
    {
        return gameObject != null
            && hiddenObjects.TryGetValue((nint)gameObject, out var record)
            && record.IsSameObject(readIdentity(gameObject));
    }

    public bool IsHidden(PlayerObjectIdentity identity)
    {
        return hiddenObjects.TryGetValue(identity.Address, out var record) && record.IsSameObject(identity);
    }

    public void CollectHiddenPlayerAddresses(GameObjectManager* manager, List<nint> addresses)
    {
        if (manager == null || hiddenObjects.Count == 0)
        {
            return;
        }

        foreach (var (address, record) in hiddenObjects)
        {
            if (record.ObjectKind != ObjectKind.Pc || !record.IsLiveAtRecordedIndex(manager, address, readIdentity))
            {
                continue;
            }

            addresses.Add(address);
        }
    }

    private bool TryGetLiveRecord(GameObject* gameObject, out HiddenObjectRecord record)
    {
        if (gameObject == null)
        {
            record = default;
            return false;
        }

        return hiddenObjects.TryGetValue((nint)gameObject, out record) && record.IsSameObject(readIdentity(gameObject));
    }

    internal int? GetRecordedObjectIndex(PlayerObjectIdentity identity) =>
        hiddenObjects.TryGetValue(identity.Address, out var record) && record.IsSameObject(identity) ? record.ObjectIndex : null;

    private static int FindLiveObjectIndexByAddress(
        GameObjectManager* manager,
        nint address,
        HiddenObjectRecord record,
        IdentityReader readIdentity
    )
    {
        if (manager == null || address == nint.Zero)
        {
            return -1;
        }

        for (var i = 0; i < manager->Objects.IndexSorted.Length; i++)
        {
            var gameObject = manager->Objects.IndexSorted[i].Value;
            if ((nint)gameObject == address && gameObject != null && record.IsSameObject(readIdentity(gameObject)))
            {
                return i;
            }
        }

        return -1;
    }

    private readonly record struct HiddenObjectRecord(
        ulong GameObjectId,
        uint EntityId,
        ObjectKind ObjectKind,
        VisibilityFlags AddedFlags,
        int ObjectIndex
    )
    {
        public static HiddenObjectRecord From(
            GameObject* gameObject,
            PlayerObjectIdentity identity,
            VisibilityFlags targetFlags,
            int objectIndex
        )
        {
            var addedFlags = targetFlags & ~gameObject->RenderFlags;
            return new(identity.GameObjectId, identity.EntityId, gameObject->ObjectKind, addedFlags, objectIndex);
        }

        public bool IsLiveAtRecordedIndex(GameObjectManager* manager, nint address, IdentityReader readIdentity)
        {
            if (manager == null || ObjectIndex < 0 || ObjectIndex >= manager->Objects.IndexSorted.Length)
            {
                return false;
            }

            var gameObject = manager->Objects.IndexSorted[ObjectIndex].Value;
            return (nint)gameObject == address && gameObject != null && IsSameObject(readIdentity(gameObject));
        }

        public bool IsSameObject(PlayerObjectIdentity identity) => identity.GameObjectId == GameObjectId && identity.EntityId == EntityId;
    }
}
