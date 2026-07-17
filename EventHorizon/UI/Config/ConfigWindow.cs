using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using EventHorizon.Localization;
using EventHorizon.Preview;
using EventHorizon.Settings;

namespace EventHorizon.UI.Config;

internal partial class ConfigWindow : Window
{
    public enum Tab
    {
        Culling,
        Behavior,
    }

    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly IDataManager dataManager;
    private readonly PlayerPreviewPanel playerPreviewPanel;
    private readonly Func<bool> isPlayerPreviewWindowOpen;
    private readonly Action togglePlayerPreviewWindow;

    private Tab? pendingSelectedTab;
    private PlayerKeepRuleId? draggedKeepRule;
    private float cullingLeftColumnWidth = 690f;
    private bool keepRuleOrderChanged;
    private bool showRaceSexEditor;
    private bool capturingTemporaryShowAllPlayersShortcut;
    private readonly HashSet<int> capturedTemporaryShowAllPlayersKeys = [];

    private readonly record struct ImGuiItemState(bool Hovered, bool Active = false);

    #region Lifecycle

    internal ConfigWindow(
        Plugin plugin,
        IDataManager dataManager,
        PlayerPreviewPanel playerPreviewPanel,
        Func<bool> isPlayerPreviewWindowOpen,
        Action togglePlayerPreviewWindow
    )
        : base($"{Loc.Text("Config.Title")}###EventHorizonConfig")
    {
        Size = new Vector2(960, 780);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        this.dataManager = dataManager;
        this.playerPreviewPanel = playerPreviewPanel;
        this.isPlayerPreviewWindowOpen = isPlayerPreviewWindowOpen;
        this.togglePlayerPreviewWindow = togglePlayerPreviewWindow;
        configuration = plugin.Configuration;
    }

    public void Open(Tab tab)
    {
        pendingSelectedTab = tab;
        IsOpen = true;
    }

    #endregion

    #region Draw

    public override void PreDraw()
    {
        WindowName = $"{Loc.Text("Config.Title")}###EventHorizonConfig";
    }

    public override void Draw()
    {
        DrawTabBar();

        pendingSelectedTab = null;
    }

    private void DrawTabBar()
    {
        if (!ImGui.BeginTabBar("###EventHorizonConfigTabs"))
        {
            return;
        }

        var tabToSelect = pendingSelectedTab;

        if (
            ImGui.BeginTabItem(
                $"{Loc.Text("Config.Tab.Culling")}###CullingTab",
                tabToSelect == Tab.Culling ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None
            )
        )
        {
            DrawCullingTab();
            ImGui.EndTabItem();
        }

        if (
            ImGui.BeginTabItem(
                $"{Loc.Text("Config.Tab.Behavior")}###BehaviorTab",
                tabToSelect == Tab.Behavior ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None
            )
        )
        {
            DrawBehaviorTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    #endregion

    #region Persistence

    private void SaveAndRefresh()
    {
        configuration.Save();
        plugin.RefreshObjectCulling(resetRuleState: true);
        plugin.RefreshDtrBar();
    }

    private void SaveAndRefreshWithoutRuleReset()
    {
        configuration.Save();
        plugin.RefreshObjectCulling();
        plugin.RefreshDtrBar();
    }

    #endregion

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

    private static void DrawCard(string title, Action content, Action? headerAction = null, float? headerActionWidth = null)
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

    private static void DrawCardHeader(string title, Action? headerAction, float? headerActionWidth)
    {
        if (headerAction is null)
        {
            ImGui.TextUnformatted(title);
            return;
        }

        var actionWidth = headerActionWidth ?? 150f;
        if (!ImGui.BeginTable($"###CardHeader{title}", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TextUnformatted(title);
            ImGui.SameLine();
            headerAction();
            return;
        }

        ImGui.TableSetupColumn("###CardHeaderTitle", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("###CardHeaderAction", ImGuiTableColumnFlags.WidthFixed, actionWidth);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(title);
        ImGui.TableNextColumn();
        headerAction();
        ImGui.EndTable();
    }

    private static void DrawFramedCard(string id, Action content)
    {
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();

        var cardWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X - 6f);
        var padding = new Vector2(12f, 10f);
        var rightPadding = 18f;
        var contentWidth = Math.Max(1f, cardWidth - padding.X - rightPadding);

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.PushID(id);
        ImGui.SetCursorScreenPos(start + padding);
        ImGui.BeginGroup();

        if (
            ImGui.BeginTable(
                "##CardContent",
                1,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings,
                new Vector2(contentWidth, 0f)
            )
        )
        {
            ImGui.TableSetupColumn("##Content", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            var contentRight = ImGui.GetCursorPosX() + contentWidth;
            ImGui.PushTextWrapPos(contentRight);
            content();
            ImGui.PopTextWrapPos();

            ImGui.EndTable();
        }

        ImGui.EndGroup();
        ImGui.PopID();

        var contentMax = ImGui.GetItemRectMax();
        var minimumHeight = ImGui.GetTextLineHeightWithSpacing() + (padding.Y * 2f);
        var height = Math.Max(minimumHeight, contentMax.Y - start.Y + padding.Y);
        var end = new Vector2(start.X + cardWidth, start.Y + height);

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
        var width = Math.Max(1f, ImGui.GetContentRegionAvail().X);

        var gapHeight = Math.Max(1f, ImGui.GetStyle().ItemSpacing.Y);
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

    private static bool DrawAutoFitText(string text)
    {
        var availableWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availableWidth);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();

        return ImGui.IsItemClicked();
    }

    private static bool DrawAutoFitCheckbox(string id, string label, ref bool value)
    {
        ImGui.PushID(id);
        var changed = ImGui.Checkbox("##Value", ref value);
        ImGui.SameLine();
        if (DrawAutoFitText(label))
        {
            value = !value;
            changed = true;
        }

        ImGui.PopID();
        return changed;
    }

    #endregion
}
