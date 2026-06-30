using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using EventHorizon.Culling.Rules;
using EventHorizon.Localization;
using EventHorizon.Preview.UI;
using EventHorizon.Settings;

namespace EventHorizon.UI.Config;

public partial class ConfigWindow : Window, IDisposable
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
    private readonly System.Func<bool> isPlayerPreviewWindowOpen;
    private readonly System.Action togglePlayerPreviewWindow;

    private Tab? pendingSelectedTab;
    private PlayerKeepRuleId? draggedKeepRule;
    private float cullingLeftColumnWidth = 690f;
    private bool keepRuleOrderChanged;
    private bool showRaceSexEditor;

    private readonly record struct ImGuiItemState(bool Hovered, bool Active = false);

    #region Lifecycle

    internal ConfigWindow(
        Plugin plugin,
        IDataManager dataManager,
        PlayerPreviewPanel playerPreviewPanel,
        System.Func<bool> isPlayerPreviewWindowOpen,
        System.Action togglePlayerPreviewWindow
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

    public void Dispose() { }

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

    private void DrawBehaviorTab()
    {
        DrawCard(
            Loc.Text("Config.Tab.Behavior"),
            () =>
            {
                var showDtrBar = configuration.ShowDtrBar;
                if (ImGui.Checkbox(Loc.Text("Config.ShowDtrBar"), ref showDtrBar))
                {
                    configuration.ShowDtrBar = showDtrBar;
                    SaveAndRefreshDtrBar();
                }

                var enableFadeTransitions = configuration.EnableFadeTransitions;
                if (ImGui.Checkbox(Loc.Text("Config.EnableFadeTransitions"), ref enableFadeTransitions))
                {
                    configuration.EnableFadeTransitions = enableFadeTransitions;
                    SaveAndRefresh();
                }
            }
        );
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

    private void SaveAndRefreshDtrBar()
    {
        configuration.Save();
        plugin.RefreshDtrBar();
    }

    #endregion
}
