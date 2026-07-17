using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using EventHorizon.Localization;
using EventHorizon.Settings;

namespace EventHorizon.Preview;

internal sealed class PlayerPreviewPanel(
    Func<PlayerPreviewSnapshot> getSnapshot,
    Action refreshPreview,
    Action<uint?> setSelectedPlayer,
    IGameGui gameGui
)
{
    private const int FastRefreshIntervalMs = 33;
    private const float CardContentRightPadding = 18f;
    private const float CardContentBottomPadding = 10f;
    private const float InlineHelpSpacing = 4f;
    private const float InlineHeightSafetyMargin = 16f;
    private const float MinimumPreviewSide = 180f;

    private readonly PlayerPreviewRenderer renderer = new();

    private long nextRefresh;
    private int worldArrowFrame = -1;
    private int selectionRouteFrame = -1;
    private uint? routedSelectedPlayerEntityId;

    public void DrawInlineContent(Func<PlayerKeepRuleId, string> getRuleLabel)
    {
        RefreshIfNeeded();
        var available = ImGui.GetContentRegionAvail();
        var availableWidth = available.X - CardContentRightPadding;
        var availableHeight =
            available.Y - ImGui.GetTextLineHeightWithSpacing() - InlineHelpSpacing - CardContentBottomPadding - InlineHeightSafetyMargin;
        var side = Math.Max(MinimumPreviewSide, Math.Min(availableWidth, availableHeight));
        DrawPreview(side, getRuleLabel);
        AddVerticalSpace(InlineHelpSpacing);
        DrawHelpText(Loc.Text("Config.Preview.PerformanceNote"));
    }

    public void DrawFloatingContent(Func<PlayerKeepRuleId, string> getRuleLabel)
    {
        RefreshIfNeeded();
        var available = ImGui.GetContentRegionAvail();
        var side = Math.Max(MinimumPreviewSide, Math.Min(available.X, available.Y));
        var offsetX = Math.Max(0f, (available.X - side) * 0.5f);
        if (offsetX > 0f)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);
        }

        DrawPreview(side, getRuleLabel);
    }

    private void DrawPreview(float side, Func<PlayerKeepRuleId, string> getRuleLabel)
    {
        var renderResult = renderer.Draw(getSnapshot(), side, getRuleLabel);
        RouteSelectedPlayer(renderResult.SelectedPlayerEntityId);

        if (renderResult.PointedPlayer.HasValue)
        {
            DrawWorldArrow(renderResult.PointedPlayer.Value);
        }
    }

    private void RouteSelectedPlayer(uint? selectedPlayerEntityId)
    {
        var frame = ImGui.GetFrameCount();
        if (selectionRouteFrame == frame && routedSelectedPlayerEntityId == selectedPlayerEntityId)
        {
            return;
        }

        selectionRouteFrame = frame;
        routedSelectedPlayerEntityId = selectedPlayerEntityId;
        setSelectedPlayer(selectedPlayerEntityId);
    }

    private void DrawWorldArrow(PlayerPreviewEntry player)
    {
        var frame = ImGui.GetFrameCount();
        if (worldArrowFrame == frame)
        {
            return;
        }

        worldArrowFrame = frame;
        PlayerPreviewWorldArrowRenderer.Draw(player, gameGui);
    }

    private void RefreshIfNeeded()
    {
        var now = Environment.TickCount64;
        if (now < nextRefresh)
        {
            return;
        }

        refreshPreview();
        nextRefresh = now + FastRefreshIntervalMs;
    }

    private static void AddVerticalSpace(float height)
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + height);
    }

    private static void DrawHelpText(string text)
    {
        ImGui.TextDisabled(text);

        if (ImGui.IsItemHovered() && ImGui.CalcTextSize(text).X > ImGui.GetItemRectSize().X)
        {
            ImGui.SetTooltip(text);
        }
    }
}
