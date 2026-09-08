using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal readonly record struct PlayerAdmissionDecision(
    PlayerObjectIdentity Identity,
    int ObjectIndex,
    bool Allowed,
    PlayerKeepDecision Decision,
    bool CutByBudget
)
{
    public unsafe GameObject* Resolve(GameObjectManager* manager)
    {
        if (manager == null || (uint)ObjectIndex >= manager->Objects.IndexSorted.Length)
            return null;

        // Manager slots can be reused between admission and UI/marker updates.
        var obj = manager->Objects.IndexSorted[ObjectIndex].Value;
        return Identity.Matches(obj) ? obj : null;
    }
}

internal readonly record struct PlayerObjectIdentity(nint Address, ulong GameObjectId, uint EntityId)
{
    public static unsafe PlayerObjectIdentity From(GameObject* gameObject) =>
        new((nint)gameObject, (ulong)gameObject->GetGameObjectId(), gameObject->EntityId);

    public unsafe bool Matches(GameObject* gameObject) =>
        gameObject != null
        && (nint)gameObject == Address
        && (ulong)gameObject->GetGameObjectId() == GameObjectId
        && gameObject->EntityId == EntityId;
}
