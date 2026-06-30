using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using EventHorizon.Localization;
using EventHorizon.ObjectTable;

namespace EventHorizon.Windows;

internal sealed class PlayerPreviewRenderer
{
    private uint? selectedPlayerEntityId;
    private int tooltipFrame = -1;
    private float viewRange = PlayerPreviewConstants.DefaultViewRange;

    public PlayerPreviewEntry? Draw(PlayerPreviewSnapshot snapshot, float side, Func<PlayerKeepRuleId, string> getRuleLabel)
    {
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(side, side);
        var end = start + size;
        var center = start + (size * 0.5f);
        var rangeRadius = Math.Max(PlayerPreviewConstants.MinimumRange, (side * 0.5f) - PlayerPreviewConstants.PreviewOuterPadding);
        var effectiveViewRange = GetEffectiveViewRange(snapshot);

        UpdateZoom(start, end, ref effectiveViewRange);

        drawList.AddCircle(
            center,
            rangeRadius,
            ImGui.GetColorU32(ImGuiCol.TextDisabled),
            PlayerPreviewConstants.RangeCircleSegments,
            PlayerPreviewConstants.RangeCircleThickness
        );
        DrawNearbyRangeCircle(drawList, snapshot, effectiveViewRange, center, rangeRadius);
        drawList.AddCircleFilled(
            center,
            PlayerPreviewConstants.LocalPlayerDotRadius,
            ImGui.GetColorU32(ImGuiCol.Text),
            PlayerPreviewConstants.DotCircleSegments
        );

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
                (hoveredPlayer.HasValue && player.Equals(hoveredPlayer.Value)) || isSelected
                    ? PlayerPreviewConstants.HoveredPlayerDotRadius
                    : PlayerPreviewConstants.PlayerDotRadius;
            drawList.AddCircleFilled(position, radius, color, PlayerPreviewConstants.DotCircleSegments);

            if (isSelected)
            {
                drawList.AddCircle(
                    position,
                    radius + PlayerPreviewConstants.SelectedPlayerRingPadding,
                    ImGui.GetColorU32(ImGuiCol.NavHighlight),
                    PlayerPreviewConstants.DotCircleSegments,
                    PlayerPreviewConstants.SelectedPlayerRingThickness
                );
            }

            if (player.CutByBudget)
            {
                drawList.AddCircle(
                    position,
                    radius + PlayerPreviewConstants.BudgetCutRingPadding,
                    ImGui.GetColorU32(ImGuiCol.PlotHistogramHovered),
                    PlayerPreviewConstants.DotCircleSegments,
                    PlayerPreviewConstants.BudgetCutRingThickness
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
        return pointedPlayer;
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

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            selectedPlayerEntityId = null;
            return;
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
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
        var bestDistanceSq = PlayerPreviewConstants.HoverRadius * PlayerPreviewConstants.HoverRadius;
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

        viewRange = Math.Clamp(viewRange, PlayerPreviewConstants.MinimumViewRange, PlayerPreviewConstants.MaximumViewRange);
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

        viewRange = Math.Clamp(
            viewRange / MathF.Pow(PlayerPreviewConstants.MouseWheelZoomStep, wheel),
            PlayerPreviewConstants.MinimumViewRange,
            PlayerPreviewConstants.MaximumViewRange
        );
        effectiveViewRange = viewRange;
    }

    private static Vector2 MapToPreview(Vector2 relativeXz, float viewRange, Vector2 center, float rangeRadius)
    {
        var distance = relativeXz.Length();
        if (distance > viewRange && distance > 0f)
        {
            relativeXz *= viewRange / distance;
        }

        var scale = rangeRadius / Math.Max(PlayerPreviewConstants.MinimumRange, viewRange);
        return center + new Vector2(relativeXz.X, relativeXz.Y) * scale;
    }

    private static void DrawNearbyRangeCircle(
        ImDrawListPtr drawList,
        PlayerPreviewSnapshot snapshot,
        float viewRange,
        Vector2 center,
        float rangeRadius
    )
    {
        if (snapshot.NearbyRange <= PlayerPreviewConstants.DisabledNearbyRange)
        {
            return;
        }

        var radius =
            rangeRadius
            * Math.Clamp(
                snapshot.NearbyRange / Math.Max(PlayerPreviewConstants.MinimumRange, viewRange),
                PlayerPreviewConstants.DisabledNearbyRange,
                PlayerPreviewConstants.MinimumRange
            );
        drawList.AddCircle(
            center,
            radius,
            ImGui.GetColorU32(ImGuiCol.PlotHistogram),
            PlayerPreviewConstants.RangeCircleSegments,
            PlayerPreviewConstants.RangeCircleThickness
        );
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
