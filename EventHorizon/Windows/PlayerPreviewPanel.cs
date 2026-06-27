using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using EventHorizon.Localization;
using EventHorizon.ObjectTable;

namespace EventHorizon.Windows;

internal sealed class PlayerPreviewPanel(Plugin plugin, IGameGui gameGui)
{
    private readonly PlayerPreviewRenderer renderer = new();

    private long nextRefresh;
    private int worldArrowFrame = -1;

    public void DrawInlineContent(Func<PlayerKeepRuleId, string> getRuleLabel)
    {
        RefreshIfNeeded();
        var side = Math.Max(
            PlayerPreviewConstants.MinimumPreviewSide,
            ImGui.GetContentRegionAvail().X - PlayerPreviewConstants.CardContentRightPadding
        );
        DrawPreview(side, getRuleLabel);
        AddVerticalSpace(4f);
        DrawHelpText(Loc.Text("Config.Preview.PerformanceNote"));
    }

    public void DrawFloatingContent(Func<PlayerKeepRuleId, string> getRuleLabel)
    {
        RefreshIfNeeded();
        var available = ImGui.GetContentRegionAvail();
        var side = Math.Max(PlayerPreviewConstants.MinimumPreviewSide, Math.Min(available.X, available.Y));
        var offsetX = Math.Max(0f, (available.X - side) * 0.5f);
        if (offsetX > 0f)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);
        }

        DrawPreview(side, getRuleLabel);
    }

    private void DrawPreview(float side, Func<PlayerKeepRuleId, string> getRuleLabel)
    {
        var pointedPlayer = renderer.Draw(plugin.PlayerPreviewSnapshot, side, getRuleLabel);
        if (pointedPlayer.HasValue)
        {
            DrawWorldArrow(pointedPlayer.Value);
        }
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

        plugin.RefreshPlayerPreview();
        nextRefresh = now + PlayerPreviewConstants.FastRefreshIntervalMs;
    }

    private static void AddVerticalSpace(float height)
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + height);
    }

    private static void DrawHelpText(string text)
    {
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }
}
