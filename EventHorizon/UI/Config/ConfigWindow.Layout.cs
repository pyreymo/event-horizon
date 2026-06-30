using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace EventHorizon.UI.Config;

public partial class ConfigWindow
{
    #region Layout Helpers

    private static void AddVerticalSpace(float height)
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + height);
    }

    private static void CenterCursorYInRow(float rowY, float rowHeight, float itemHeight)
    {
        var cursor = ImGui.GetCursorScreenPos();
        var centeredY = rowY + Math.Max(0f, (rowHeight - itemHeight) * 0.5f);
        ImGui.SetCursorScreenPos(new Vector2(cursor.X, centeredY));
    }

    private static void DrawCard(string title, System.Action content, System.Action? headerAction = null, float? headerActionWidth = null)
    {
        AddVerticalSpace(8f);
        DrawFramedCard(
            $"###Card{title}",
            () =>
            {
                DrawCardHeader(title, headerAction, headerActionWidth);
                DrawCardSeparator();
                content();
            }
        );
    }

    private static void DrawCardHeader(string title, System.Action? headerAction, float? headerActionWidth)
    {
        if (headerAction is null)
        {
            ImGui.TextUnformatted(title);
            return;
        }

        var actionWidth = headerActionWidth ?? 150f;
        if (!ImGui.BeginTable($"###CardHeader{title}", 3, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TextUnformatted(title);
            ImGui.SameLine();
            headerAction();
            return;
        }

        ImGui.TableSetupColumn("###CardHeaderTitle", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("###CardHeaderAction", ImGuiTableColumnFlags.WidthFixed, actionWidth);
        ImGui.TableSetupColumn("###CardHeaderPadding", ImGuiTableColumnFlags.WidthFixed, 12f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(title);
        ImGui.TableNextColumn();
        headerAction();
        ImGui.TableNextColumn();
        ImGui.EndTable();
    }

    private static void DrawFramedCard(string id, System.Action content)
    {
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = Math.Max(1f, ImGui.GetContentRegionAvail().X - 6f);
        var padding = new Vector2(12f, 10f);
        var rightPadding = 18f;

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.PushID(id);
        ImGui.SetCursorScreenPos(start + padding);
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(start.X + width - rightPadding);
        content();
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        ImGui.PopID();

        var contentMax = ImGui.GetItemRectMax();
        var height = Math.Max(ImGui.GetTextLineHeightWithSpacing() + (padding.Y * 2f), contentMax.Y - start.Y + padding.Y);
        var end = new Vector2(start.X + width, start.Y + height);

        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(ImGuiCol.ChildBg), 6f);
        drawList.AddRect(start, end, ImGui.GetColorU32(ImGuiCol.Border), 6f);
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
    }

    private static void DrawCardSeparator()
    {
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = Math.Max(1f, ImGui.GetContentRegionAvail().X - 18f);
        var gapHeight = 9f;
        var y = start.Y + (gapHeight * 0.5f);
        drawList.AddLine(new Vector2(start.X, y), new Vector2(start.X + width, y), ImGui.GetColorU32(ImGuiCol.Separator));
        ImGui.Dummy(new Vector2(width, gapHeight));
    }

    private static void DrawHelpText(string text)
    {
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }

    #endregion
}
