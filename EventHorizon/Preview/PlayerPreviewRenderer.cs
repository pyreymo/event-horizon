using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using EventHorizon.Localization;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Preview;

internal sealed class PlayerPreviewRenderer
{
    private const float DefaultViewRange = 50f;
    private const float MinimumViewRange = 10f;
    private const float MaximumViewRange = 120f;
    private const float MouseWheelZoomStep = 1.15f;
    private const float MinimumRange = 1f;
    private const float DisabledNearbyRange = 0f;
    private const float PreviewOuterPadding = 14f;
    private const int RangeCircleSegments = 64;
    private const int DotCircleSegments = 16;
    private const float RangeCircleThickness = 1.2f;
    private const float BudgetCutRingThickness = 1.4f;
    private const float SelectedPlayerRingThickness = 2f;
    private const float LocalPlayerDotRadius = 4f;
    private const float PlayerDotRadius = 4f;
    private const float HoveredPlayerDotRadius = 6f;
    private const float HoverRadius = 7f;
    private const float BudgetCutRingPadding = 2f;
    private const float SelectedPlayerRingPadding = 4f;

    private uint? selectedPlayerEntityId;
    private int tooltipFrame = -1;
    private float viewRange = DefaultViewRange;

    public PlayerPreviewRenderResult Draw(PlayerPreviewSnapshot snapshot, float side, Func<PlayerKeepRuleId, string> getRuleLabel)
    {
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(side, side);
        var end = start + size;
        var center = start + (size * 0.5f);
        var rangeRadius = Math.Max(MinimumRange, (side * 0.5f) - PreviewOuterPadding);
        var effectiveViewRange = GetEffectiveViewRange(snapshot);

        UpdateZoom(start, end, ref effectiveViewRange);

        drawList.AddCircle(center, rangeRadius, ImGui.GetColorU32(ImGuiCol.TextDisabled), RangeCircleSegments, RangeCircleThickness);
        DrawNearbyRangeCircle(drawList, snapshot, effectiveViewRange, center, rangeRadius);
        drawList.AddCircleFilled(center, LocalPlayerDotRadius, ImGui.GetColorU32(ImGuiCol.Text), DotCircleSegments);

        var hoveredPlayer = FindHoveredPlayer(snapshot, center, rangeRadius);
        UpdateSelectedPlayer(snapshot, start, end, hoveredPlayer);
        var selectedPlayer = FindSelectedPlayer(snapshot);
        var pointedPlayer = selectedPlayer ?? hoveredPlayer;
        foreach (var player in snapshot.Players)
        {
            var position = MapToPreview(player.RelativeXZ, effectiveViewRange, center, rangeRadius);
            var color = GetPlayerColor(player);
            var isSelected = IsSelected(player);
            var radius =
                (hoveredPlayer.HasValue && player.Equals(hoveredPlayer.Value)) || isSelected ? HoveredPlayerDotRadius : PlayerDotRadius;
            drawList.AddCircleFilled(position, radius, color, DotCircleSegments);

            if (isSelected)
            {
                drawList.AddCircle(
                    position,
                    radius + SelectedPlayerRingPadding,
                    ImGui.GetColorU32(ImGuiCol.NavHighlight),
                    DotCircleSegments,
                    SelectedPlayerRingThickness
                );
            }

            if (player.CutByBudget)
            {
                drawList.AddCircle(
                    position,
                    radius + BudgetCutRingPadding,
                    ImGui.GetColorU32(ImGuiCol.PlotHistogramHovered),
                    DotCircleSegments,
                    BudgetCutRingThickness
                );
            }
        }

        if (selectedPlayer.HasValue)
        {
            DrawTooltipOnce(selectedPlayer.Value, getRuleLabel);
        }
        else if (hoveredPlayer.HasValue)
        {
            DrawTooltipOnce(hoveredPlayer.Value, getRuleLabel);
        }

        if (snapshot.Players.Count == 0)
        {
            var text = Loc.Text("Config.Preview.Empty");
            var textSize = ImGui.CalcTextSize(text);
            drawList.AddText(center - (textSize * 0.5f), ImGui.GetColorU32(ImGuiCol.TextDisabled), text);
        }

        ImGui.Dummy(size);
        return new(pointedPlayer, selectedPlayer?.EntityId);
    }

    private void UpdateSelectedPlayer(
        PlayerPreviewSnapshot snapshot,
        Vector2 previewMin,
        Vector2 previewMax,
        PlayerPreviewEntry? hoveredPlayer
    )
    {
        if (selectedPlayerEntityId.HasValue && !HasSelectedPlayer(snapshot))
        {
            selectedPlayerEntityId = null;
        }

        if (!ImGui.IsMouseHoveringRect(previewMin, previewMax))
        {
            return;
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (hoveredPlayer.HasValue && IsSelected(hoveredPlayer.Value))
            {
                selectedPlayerEntityId = null;
                return;
            }

            selectedPlayerEntityId = hoveredPlayer?.EntityId;
        }
    }

    private PlayerPreviewEntry? FindSelectedPlayer(PlayerPreviewSnapshot snapshot)
    {
        if (!selectedPlayerEntityId.HasValue)
        {
            return null;
        }

        foreach (var player in snapshot.Players)
        {
            if (IsSelected(player))
            {
                return player;
            }
        }

        return null;
    }

    private bool HasSelectedPlayer(PlayerPreviewSnapshot snapshot)
    {
        foreach (var player in snapshot.Players)
        {
            if (IsSelected(player))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsSelected(PlayerPreviewEntry player) => selectedPlayerEntityId == player.EntityId;

    private PlayerPreviewEntry? FindHoveredPlayer(PlayerPreviewSnapshot snapshot, Vector2 center, float rangeRadius)
    {
        var mouse = ImGui.GetMousePos();
        PlayerPreviewEntry? hoveredPlayer = null;
        var bestDistanceSq = HoverRadius * HoverRadius;
        var effectiveViewRange = GetEffectiveViewRange(snapshot);

        foreach (var player in snapshot.Players)
        {
            var position = MapToPreview(player.RelativeXZ, effectiveViewRange, center, rangeRadius);
            var distanceSq = Vector2.DistanceSquared(mouse, position);
            if (distanceSq > bestDistanceSq)
            {
                continue;
            }

            bestDistanceSq = distanceSq;
            hoveredPlayer = player;
        }

        return hoveredPlayer;
    }

    private float GetEffectiveViewRange(PlayerPreviewSnapshot snapshot)
    {
        if (viewRange <= 0f)
        {
            viewRange = snapshot.ViewRange;
        }

        viewRange = Math.Clamp(viewRange, MinimumViewRange, MaximumViewRange);
        return viewRange;
    }

    private void UpdateZoom(Vector2 min, Vector2 max, ref float effectiveViewRange)
    {
        if (!ImGui.IsMouseHoveringRect(min, max))
        {
            return;
        }

        var wheel = ImGui.GetIO().MouseWheel;
        if (wheel == 0f)
        {
            return;
        }

        viewRange = Math.Clamp(viewRange / MathF.Pow(MouseWheelZoomStep, wheel), MinimumViewRange, MaximumViewRange);
        effectiveViewRange = viewRange;
    }

    private static Vector2 MapToPreview(Vector2 relativeXz, float viewRange, Vector2 center, float rangeRadius)
    {
        var distance = relativeXz.Length();
        if (distance > viewRange && distance > 0f)
        {
            relativeXz *= viewRange / distance;
        }

        var scale = rangeRadius / Math.Max(MinimumRange, viewRange);
        return center + (new Vector2(relativeXz.X, relativeXz.Y) * scale);
    }

    private static void DrawNearbyRangeCircle(
        ImDrawListPtr drawList,
        PlayerPreviewSnapshot snapshot,
        float viewRange,
        Vector2 center,
        float rangeRadius
    )
    {
        if (snapshot.NearbyRange <= DisabledNearbyRange)
        {
            return;
        }

        var radius = rangeRadius * Math.Clamp(snapshot.NearbyRange / Math.Max(MinimumRange, viewRange), DisabledNearbyRange, MinimumRange);
        drawList.AddCircle(center, radius, ImGui.GetColorU32(ImGuiCol.PlotHistogram), RangeCircleSegments, RangeCircleThickness);
    }

    private static uint GetPlayerColor(PlayerPreviewEntry player)
    {
        if (!player.IsVisible)
        {
            return ImGui.GetColorU32(ImGuiCol.TextDisabled);
        }

        return player.BudgetPolicy == PlayerKeepBudgetPolicy.Exempt
            ? ImGui.GetColorU32(ImGuiCol.CheckMark)
            : ImGui.GetColorU32(ImGuiCol.PlotHistogram);
    }

    private void DrawTooltipOnce(PlayerPreviewEntry player, Func<PlayerKeepRuleId, string> getRuleLabel)
    {
        var frame = ImGui.GetFrameCount();
        if (tooltipFrame == frame)
        {
            return;
        }

        tooltipFrame = frame;
        DrawTooltip(player, getRuleLabel);
    }

    private static void DrawTooltip(PlayerPreviewEntry player, Func<PlayerKeepRuleId, string> getRuleLabel)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(player.Name);
        ImGui.Separator();
        ImGui.TextUnformatted(string.Format(Loc.Text("Config.Preview.Tooltip.Distance"), player.Distance));
        ImGui.TextUnformatted(Loc.Text(player.IsVisible ? "Config.Preview.Tooltip.Visible" : "Config.Preview.Tooltip.Hidden"));

        if (player.BestRule.HasValue)
        {
            ImGui.TextUnformatted(string.Format(Loc.Text("Config.Preview.Tooltip.Rule"), getRuleLabel(player.BestRule.Value)));
            ImGui.TextUnformatted(
                Loc.Text(
                    player.BudgetPolicy == PlayerKeepBudgetPolicy.Counted
                        ? "Config.Preview.Tooltip.Budget.Counted"
                        : "Config.Preview.Tooltip.Budget.Exempt"
                )
            );
        }
        else
        {
            ImGui.TextUnformatted(Loc.Text("Config.Preview.Tooltip.NoRule"));
        }

        if (player.CutByBudget)
        {
            ImGui.TextUnformatted(Loc.Text("Config.Preview.Tooltip.CutByBudget"));
        }

        ImGui.EndTooltip();
    }
}

internal readonly record struct PlayerPreviewRenderResult(PlayerPreviewEntry? PointedPlayer, uint? SelectedPlayerEntityId);

internal static unsafe class PlayerPreviewWorldArrowRenderer
{
    private static readonly Vector4 WorldArrowColor = new(1f, 0.5f, 0f, 1f);
    private const float WorldArrowLineThickness = 2f;
    private const float WorldArrowHeadLength = 12f;
    private const float WorldArrowHeadHalfWidth = 5f;
    private const float WorldArrowTargetRadius = 3f;
    private const float WorldArrowScreenEdgePadding = 24f;

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

        var color = ImGui.GetColorU32(WorldArrowColor);
        var drawList = ImGui.GetBackgroundDrawList();

        drawList.AddLine(sourceScreenPos, arrowEnd, color, WorldArrowLineThickness);
        DrawArrowHead(drawList, sourceScreenPos, arrowEnd, color);
        drawList.AddCircleFilled(arrowEnd, WorldArrowTargetRadius, color);
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
        var padding = WorldArrowScreenEdgePadding;
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
        var headLength = Math.Min(WorldArrowHeadLength, length * 0.45f);
        var headHalfWidth = Math.Min(WorldArrowHeadHalfWidth, headLength * 0.5f);
        var normal = new Vector2(-direction.Y, direction.X);
        var headBase = to - (direction * headLength);

        drawList.AddLine(to, headBase + (normal * headHalfWidth), color, WorldArrowLineThickness);
        drawList.AddLine(to, headBase - (normal * headHalfWidth), color, WorldArrowLineThickness);
    }
}
