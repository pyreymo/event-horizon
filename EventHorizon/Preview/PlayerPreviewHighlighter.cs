using System;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Preview;

internal sealed unsafe class PlayerPreviewHighlighter : IDisposable
{
    private const ObjectHighlightColor HighlightColor = ObjectHighlightColor.Orange;

    private uint? selectedEntityId;
    private long selectedLeaseExpiresAt;
    private HighlightedObject? highlightedObject;

    public void SetSelectedPlayer(uint? entityId)
    {
        if (!entityId.HasValue)
        {
            selectedEntityId = null;
            selectedLeaseExpiresAt = 0;
            ClearHighlight();
            return;
        }

        selectedEntityId = entityId.Value;
        selectedLeaseExpiresAt = Environment.TickCount64 + PlayerPreviewConstants.SelectionVisibilityLeaseMs;
    }

    public void Update()
    {
        if (!selectedEntityId.HasValue || Environment.TickCount64 > selectedLeaseExpiresAt)
        {
            selectedEntityId = null;
            selectedLeaseExpiresAt = 0;
            ClearHighlight();
            return;
        }

        var manager = GameObjectManager.Instance();
        if (manager == null)
        {
            ClearHighlight();
            return;
        }

        var gameObject = manager->Objects.GetObjectByEntityId(selectedEntityId.Value);
        if (!IsUsableGameObject(gameObject))
        {
            ClearHighlight(manager);
            return;
        }

        if (highlightedObject.HasValue && !highlightedObject.Value.IsSameObject(gameObject))
        {
            ClearHighlight(manager);
        }

        gameObject->Highlight(HighlightColor);
        highlightedObject = HighlightedObject.From(gameObject);
    }

    public void Dispose()
    {
        ClearHighlight();
    }

    private void ClearHighlight()
    {
        ClearHighlight(GameObjectManager.Instance());
    }

    private void ClearHighlight(GameObjectManager* manager)
    {
        if (!highlightedObject.HasValue)
        {
            return;
        }

        var gameObject = FindObject(manager, highlightedObject.Value);
        highlightedObject = null;
        if (IsPluginHighlight(gameObject))
        {
            gameObject->Highlight(ObjectHighlightColor.None);
        }
    }

    private static GameObject* FindObject(GameObjectManager* manager, HighlightedObject highlighted)
    {
        if (manager == null || highlighted.Address == nint.Zero)
        {
            return null;
        }

        var entityObject = manager->Objects.GetObjectByEntityId(highlighted.EntityId);
        if (highlighted.IsSameObject(entityObject))
        {
            return entityObject;
        }

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if ((nint)gameObject == highlighted.Address && highlighted.IsSameObject(gameObject))
            {
                return gameObject;
            }
        }

        return null;
    }

    private static bool IsPluginHighlight(GameObject* gameObject)
    {
        if (!IsUsableGameObject(gameObject))
        {
            return false;
        }

        var drawObject = gameObject->GetDrawObject();
        return drawObject != null && drawObject->OutlineColor == HighlightColor;
    }

    private static bool IsUsableGameObject(GameObject* gameObject)
    {
        return gameObject != null && gameObject->VirtualTable != null;
    }

    private readonly record struct HighlightedObject(nint Address, ulong GameObjectId, uint EntityId)
    {
        public static HighlightedObject From(GameObject* gameObject)
        {
            return new((nint)gameObject, (ulong)gameObject->GetGameObjectId(), gameObject->EntityId);
        }

        public bool IsSameObject(GameObject* gameObject)
        {
            return gameObject != null && (ulong)gameObject->GetGameObjectId() == GameObjectId && gameObject->EntityId == EntityId;
        }
    }
}
