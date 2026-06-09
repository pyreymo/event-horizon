using FFXIVClientStructs.FFXIV.Client.Game.Object;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace EventHorizon.ObjectTable;

internal static unsafe class ObjectTableStats
{
    public static int CurrentPlayerCount()
    {
        return CountPlayerObjects(GameObjectManager.Instance());
    }

    public static int CountPlayerObjects(GameObjectManager* manager)
    {
        if (manager == null)
        {
            return 0;
        }

        var count = 0;
        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject != null && gameObject->ObjectKind == ObjectKind.Pc)
            {
                count++;
            }
        }

        return count;
    }
}
