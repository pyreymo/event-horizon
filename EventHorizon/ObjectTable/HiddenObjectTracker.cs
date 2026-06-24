using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.ObjectTable;

internal sealed unsafe class HiddenObjectTracker
{
    private readonly Dictionary<nint, HiddenObjectRecord> hiddenObjects = [];

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

    public void Hide(GameObject* gameObject, VisibilityFlags targetFlags)
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
            hiddenObjects[address] = HiddenObjectRecord.From(gameObject, targetFlags);
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
        if (record.IsSameObject(gameObject))
        {
            gameObject->RenderFlags &= ~record.AddedFlags;
        }
    }

    public void RestoreAll(GameObjectManager* manager)
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

    public void PruneMissing(GameObjectManager* manager)
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

    public void Clear()
    {
        hiddenObjects.Clear();
    }

    public bool IsHidden(GameObject* gameObject)
    {
        return gameObject != null && hiddenObjects.TryGetValue((nint)gameObject, out var record) && record.IsSameObject(gameObject);
    }

    private static GameObject* FindObject(GameObjectManager* manager, nint address, HiddenObjectRecord record)
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

    private readonly record struct HiddenObjectRecord(ulong GameObjectId, uint EntityId, ObjectKind ObjectKind, VisibilityFlags AddedFlags)
    {
        public static HiddenObjectRecord From(GameObject* gameObject, VisibilityFlags targetFlags)
        {
            var addedFlags = targetFlags & ~gameObject->RenderFlags;
            return new((ulong)gameObject->GetGameObjectId(), gameObject->EntityId, gameObject->ObjectKind, addedFlags);
        }

        public bool IsSameObject(GameObject* gameObject) =>
            gameObject != null && (ulong)gameObject->GetGameObjectId() == GameObjectId && gameObject->EntityId == EntityId;
    }
}
