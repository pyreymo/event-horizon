using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using EventHorizon.ObjectTable;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Windows;

internal static unsafe class PlayerPreviewWorldArrowRenderer
{
    public static void Draw(PlayerPreviewEntry player, IGameGui gameGui)
    {
        var manager = GameObjectManager.Instance();
        if (manager == null)
        {
            return;
        }

        var localPlayer = FindLocalPlayer(manager);
        var targetPlayer = FindPreviewGameObject(manager, player);
        if (localPlayer == null || targetPlayer == null)
        {
            return;
        }

        DrawWorldArrow(localPlayer, targetPlayer, gameGui);
    }

    private static GameObject* FindLocalPlayer(GameObjectManager* manager)
    {
        var indexSorted = manager->Objects.IndexSorted;
        if (indexSorted.Length == 0)
        {
            return null;
        }

        var localPlayer = indexSorted[0].Value;
        return IsUsableGameObject(localPlayer) ? localPlayer : null;
    }

    private static GameObject* FindPreviewGameObject(GameObjectManager* manager, PlayerPreviewEntry player)
    {
        var indexSorted = manager->Objects.IndexSorted;
        if ((uint)player.ObjectIndex < (uint)indexSorted.Length)
        {
            var indexedObject = indexSorted[player.ObjectIndex].Value;
            if (IsSamePreviewPlayer(indexedObject, player))
            {
                return indexedObject;
            }
        }

        var entityObject = manager->Objects.GetObjectByEntityId(player.EntityId);
        return IsSamePreviewPlayer(entityObject, player) ? entityObject : null;
    }

    private static bool IsSamePreviewPlayer(GameObject* gameObject, PlayerPreviewEntry player)
    {
        return IsUsableGameObject(gameObject) && gameObject->EntityId == player.EntityId;
    }

    private static bool IsUsableGameObject(GameObject* gameObject)
    {
        return gameObject != null && gameObject->VirtualTable != null;
    }

    private static void DrawWorldArrow(GameObject* sourceObject, GameObject* targetObject, IGameGui gameGui)
    {
        if (
            !TryGetScreenPosition(sourceObject, gameGui, out var sourceScreenPos, out _)
            || !TryGetScreenPosition(targetObject, gameGui, out var targetScreenPos, out var targetInView)
        )
        {
            return;
        }

        sourceScreenPos = ClampToViewport(sourceScreenPos);
        var arrowEnd = targetInView ? targetScreenPos : GetViewportEdgePoint(sourceScreenPos, targetScreenPos);
        if (Vector2.DistanceSquared(sourceScreenPos, arrowEnd) <= 1f)
        {
            return;
        }

        var color = ImGui.GetColorU32(PlayerPreviewConstants.WorldArrowColor);
        var drawList = ImGui.GetForegroundDrawList();

        drawList.AddLine(sourceScreenPos, arrowEnd, color, PlayerPreviewConstants.WorldArrowLineThickness);
        DrawArrowHead(drawList, sourceScreenPos, arrowEnd, color);
        drawList.AddCircleFilled(arrowEnd, PlayerPreviewConstants.WorldArrowTargetRadius, color);
    }

    private static bool TryGetScreenPosition(GameObject* gameObject, IGameGui gameGui, out Vector2 screenPos, out bool inView)
    {
        screenPos = Vector2.Zero;
        inView = false;

        if (!IsUsableGameObject(gameObject))
        {
            return false;
        }

        var position = gameObject->GetPosition();
        return position != null && gameGui.WorldToScreen((Vector3)(*position), out screenPos, out inView);
    }

    private static Vector2 ClampToViewport(Vector2 point)
    {
        return TryGetViewportRect(out var min, out var max) ? ClampToRect(point, min, max) : point;
    }

    private static Vector2 GetViewportEdgePoint(Vector2 start, Vector2 target)
    {
        if (!TryGetViewportRect(out var min, out var max))
        {
            return target;
        }

        if (IsPointInsideRect(target, min, max))
        {
            return target;
        }

        var direction = target - start;
        if (direction.LengthSquared() <= 0.01f)
        {
            return ClampToRect(target, min, max);
        }

        var bestT = float.PositiveInfinity;
        ConsiderViewportEdge((min.X - start.X) / direction.X, start, direction, min, max, ref bestT);
        ConsiderViewportEdge((max.X - start.X) / direction.X, start, direction, min, max, ref bestT);
        ConsiderViewportEdge((min.Y - start.Y) / direction.Y, start, direction, min, max, ref bestT);
        ConsiderViewportEdge((max.Y - start.Y) / direction.Y, start, direction, min, max, ref bestT);

        return float.IsPositiveInfinity(bestT) ? ClampToRect(target, min, max) : start + (direction * bestT);
    }

    private static void ConsiderViewportEdge(float t, Vector2 start, Vector2 direction, Vector2 min, Vector2 max, ref float bestT)
    {
        if (!float.IsFinite(t) || t < 0f || t >= bestT)
        {
            return;
        }

        var point = start + (direction * t);
        if (IsPointInsideRect(point, min, max))
        {
            bestT = t;
        }
    }

    private static bool TryGetViewportRect(out Vector2 min, out Vector2 max)
    {
        var padding = PlayerPreviewConstants.WorldArrowScreenEdgePadding;
        var displaySize = ImGui.GetIO().DisplaySize;

        min = new Vector2(padding, padding);
        max = new Vector2(Math.Max(padding, displaySize.X - padding), Math.Max(padding, displaySize.Y - padding));

        return displaySize.X > 0f && displaySize.Y > 0f;
    }

    private static Vector2 ClampToRect(Vector2 point, Vector2 min, Vector2 max)
    {
        return new Vector2(Math.Clamp(point.X, min.X, max.X), Math.Clamp(point.Y, min.Y, max.Y));
    }

    private static bool IsPointInsideRect(Vector2 point, Vector2 min, Vector2 max)
    {
        return point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;
    }

    private static void DrawArrowHead(ImDrawListPtr drawList, Vector2 from, Vector2 to, uint color)
    {
        var direction = to - from;
        var length = direction.Length();
        if (length <= 0.01f)
        {
            return;
        }

        direction /= length;
        var headLength = Math.Min(PlayerPreviewConstants.WorldArrowHeadLength, length * 0.45f);
        var headHalfWidth = Math.Min(PlayerPreviewConstants.WorldArrowHeadHalfWidth, headLength * 0.5f);
        var normal = new Vector2(-direction.Y, direction.X);
        var headBase = to - (direction * headLength);

        drawList.AddLine(to, headBase + (normal * headHalfWidth), color, PlayerPreviewConstants.WorldArrowLineThickness);
        drawList.AddLine(to, headBase - (normal * headHalfWidth), color, PlayerPreviewConstants.WorldArrowLineThickness);
    }
}
