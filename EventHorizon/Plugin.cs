using System;
using System.Collections.Generic;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EventHorizon.Culling;
using EventHorizon.Culling.Hooks;
using EventHorizon.Culling.Rules;
using EventHorizon.Integration.Dtr;
using EventHorizon.Integration.Layout;
using EventHorizon.Integration.NamePlate;
using EventHorizon.Integration.Vfx;
using EventHorizon.Localization;
using EventHorizon.Preview;
using EventHorizon.Preview.UI;
using EventHorizon.Settings;
using EventHorizon.UI.Config;

namespace EventHorizon;

public sealed class Plugin : IDalamudPlugin
{
    private const string PrimaryCommandName = "/eventhorizon";
    private const string ShortCommandName = "/eh";
    private const int DynamicCullingRefreshIntervalMs = 200;
    private const int DtrBarRefreshIntervalMs = 1_000;

    #region Services

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    [PluginService]
    internal static ISigScanner SigScanner { get; private set; } = null!;

    [PluginService]
    internal static IPlayerState PlayerState { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static IObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    internal static ITargetManager TargetManager { get; private set; } = null!;

    [PluginService]
    internal static IGameGui GameGui { get; private set; } = null!;

    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static ICondition Condition { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService]
    internal static INamePlateGui NamePlateGui { get; private set; } = null!;

    [PluginService]
    internal static ITextureProvider TextureProvider { get; private set; } = null!;

    [PluginService]
    internal static IDtrBar DtrBar { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    #endregion

    #region State

    internal Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("EventHorizon");
    private ConfigWindow ConfigWindow { get; init; }
    private PlayerPreviewWindow PlayerPreviewWindow { get; init; }
    private UpdateObjectArraysHook UpdateObjectArraysHook { get; init; }
    private PlayerPreviewHighlighter PlayerPreviewHighlighter { get; init; }
    private ActorVfxController ActorVfxController { get; init; }
    private StaticVfxResourceRedirector StaticVfxResourceRedirector { get; init; }
    private StaticVfxController StaticVfxController { get; init; }
    private DtrBarIntegration DtrBarIntegration { get; init; }
    private DtrBackgroundController DtrBackgroundController { get; init; }
    private LayoutGraphicsVisibilityController LayoutGraphicsVisibilityController { get; init; }
    private NamePlateTargetingMeMarkerController NamePlateTargetingMeMarkerController { get; init; }

    private long nextDynamicCullingRefresh;
    private long nextDtrBarRefresh;
    public int HiddenPlayerCount => UpdateObjectArraysHook.HiddenPlayerCount;
    internal PlayerKeepBudgetStats KeepBudgetStats => UpdateObjectArraysHook.KeepBudgetStats;
    internal PlayerPreviewSnapshot PlayerPreviewSnapshot => UpdateObjectArraysHook.PlayerPreviewSnapshot;
    public bool IsDutyCullingSuspended =>
        Configuration.HideAllOtherPlayers
        && Configuration.DisableInDuty
        && (Condition[ConditionFlag.BoundByDuty] || Condition[ConditionFlag.BoundByDuty56]);

    #endregion

    #region Lifecycle

    public Plugin()
    {
        Loc.Load(PluginInterface);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var playerPreviewPanel = new PlayerPreviewPanel(this, GameGui);
        ConfigWindow = new ConfigWindow(this, DataManager, playerPreviewPanel, IsPlayerPreviewWindowOpen, TogglePlayerPreviewWindow);
        PlayerPreviewWindow = new PlayerPreviewWindow(playerPreviewPanel, OpenMainUi);
        PlayerPreviewHighlighter = new PlayerPreviewHighlighter();
        ActorVfxController = new ActorVfxController(GameInteropProvider, SigScanner, Log);
        StaticVfxResourceRedirector = new StaticVfxResourceRedirector(PluginInterface, GameInteropProvider, Log);
        StaticVfxController = new StaticVfxController(GameInteropProvider, SigScanner, Log);
        UpdateObjectArraysHook = new UpdateObjectArraysHook(
            GameInteropProvider,
            Configuration,
            PlayerState,
            Condition,
            ObjectTable,
            TargetManager,
            GameGui,
            StaticVfxController,
            Log
        );
        DtrBarIntegration = new DtrBarIntegration(DtrBar, Configuration, GetDtrBarState, SetPlayerHidingEnabled, ToggleConfigUi);
        DtrBackgroundController = new DtrBackgroundController(AddonLifecycle, GameGui, Framework, ClientState, Configuration);
        LayoutGraphicsVisibilityController = new LayoutGraphicsVisibilityController(GameInteropProvider, ClientState, Log);
        NamePlateTargetingMeMarkerController = new NamePlateTargetingMeMarkerController(
            AddonLifecycle,
            GameGui,
            NamePlateGui,
            ObjectTable,
            TargetManager,
            Condition,
            Configuration,
            Framework,
            TextureProvider,
            ActorVfxController,
            Log
        );

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(PlayerPreviewWindow);

        CommandManager.AddHandler(PrimaryCommandName, new CommandInfo(OnCommand) { HelpMessage = Loc.Text("Command.Help.OpenSettings") });
        CommandManager.AddHandler(ShortCommandName, new CommandInfo(OnCommand) { HelpMessage = BuildCommandHelp(ShortCommandName) });

        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.LanguageChanged += OnLanguageChanged;
        ChatGui.ChatMessage += OnChatMessage;
        Framework.Update += OnFrameworkUpdate;
        UpdateObjectArraysHook.Enable();
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.LanguageChanged -= OnLanguageChanged;
        ChatGui.ChatMessage -= OnChatMessage;
        Framework.Update -= OnFrameworkUpdate;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        PlayerPreviewWindow.Dispose();
        PlayerPreviewHighlighter.Dispose();
        DtrBarIntegration.Dispose();
        DtrBackgroundController.Dispose();
        LayoutGraphicsVisibilityController.Dispose();
        NamePlateTargetingMeMarkerController.Dispose();
        UpdateObjectArraysHook.Dispose();
        StaticVfxController.Dispose();
        StaticVfxResourceRedirector.Dispose();
        ActorVfxController.Dispose();

        CommandManager.RemoveHandler(PrimaryCommandName);
        CommandManager.RemoveHandler(ShortCommandName);
    }

    #endregion

    #region UI

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                SetPlayerHidingEnabled(true);
                break;
            case "off":
                SetPlayerHidingEnabled(false);
                break;
            case "toggle":
                SetPlayerHidingEnabled(!Configuration.HideAllOtherPlayers);
                break;
            case "preview":
                TogglePlayerPreviewWindow();
                break;
            default:
                ToggleConfigUi();
                break;
        }
    }

    private static string BuildCommandHelp(string commandName)
    {
        return string.Format(
            Loc.Text("Command.Help"),
            Loc.Text("Command.Help.OpenSettings"),
            commandName,
            Loc.Text("Command.Help.Enable"),
            Loc.Text("Command.Help.Disable"),
            Loc.Text("Command.Help.Toggle"),
            Loc.Text("Command.Help.Preview")
        );
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    private bool IsPlayerPreviewWindowOpen() => PlayerPreviewWindow.IsOpen;

    private void TogglePlayerPreviewWindow()
    {
        PlayerPreviewWindow.Toggle();
    }

    private void OpenMainUi()
    {
        ConfigWindow.Open(ConfigWindow.Tab.Culling);
    }

    private void OpenConfigUi()
    {
        ConfigWindow.Open(ConfigWindow.Tab.Behavior);
    }

    public void RefreshDtrBar() => DtrBarIntegration.Refresh();

    public void RefreshDtrBackground() => DtrBackgroundController.Refresh();

    public void RequestTargetingMeMarkerRefresh() => NamePlateTargetingMeMarkerController.RequestRefresh();

    private void OnDraw()
    {
        try
        {
            WindowSystem.Draw();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WindowSystem.Draw threw.");
        }
    }

    #endregion

    #region Culling

    private void OnFrameworkUpdate(IFramework framework)
    {
        RefreshDtrBarIfNeeded();
        LayoutGraphicsVisibilityController.Update(Configuration.HideBgPartGraphicsObjects, Configuration.HideTerrainGraphicsObjects);
        PlayerPreviewHighlighter.Update();

        var runtime = UpdateObjectArraysHook.SynchronizeRuntimeMode();
        var now = Environment.TickCount64;
        var playerTopologyDirty = UpdateObjectArraysHook.ConsumePlayerTopologyDirty();
        var schedule = CullingFrameSchedule.Decide(
            runtime.Mode,
            runtime.RequiresRefresh || now >= nextDynamicCullingRefresh,
            playerTopologyDirty
        );

        if (schedule.Refresh)
        {
            RefreshObjectCulling();
            nextDynamicCullingRefresh = Environment.TickCount64 + DynamicCullingRefreshIntervalMs;
        }

        if (schedule.Tick)
        {
            UpdateObjectArraysHook.FrameworkTick();
        }
    }

    private void RefreshDtrBarIfNeeded()
    {
        var now = Environment.TickCount64;
        if (now < nextDtrBarRefresh)
        {
            return;
        }

        RefreshDtrBar();
        nextDtrBarRefresh = Environment.TickCount64 + DtrBarRefreshIntervalMs;
    }

    public void RefreshObjectCulling(bool resetRuleState = false)
    {
        UpdateObjectArraysHook.Refresh(resetRuleState);
    }

    internal void RefreshPlayerPreview()
    {
        UpdateObjectArraysHook.RefreshPlayerPreview();
    }

    internal void SetPreviewSelectedPlayer(uint? entityId)
    {
        PlayerPreviewHighlighter.SetSelectedPlayer(entityId);
        if (UpdateObjectArraysHook.SetPreviewSelectedPlayer(entityId))
        {
            RefreshObjectCulling();
        }
    }

    private void SetPlayerHidingEnabled(bool enabled)
    {
        if (Configuration.HideAllOtherPlayers == enabled)
        {
            RefreshDtrBar();
            return;
        }

        Configuration.HideAllOtherPlayers = enabled;
        Configuration.Save();
        RefreshDtrBar();
        RefreshObjectCulling(resetRuleState: true);
    }

    private void OnChatMessage(IChatMessage message)
    {
        UpdateObjectArraysHook.RecordChatMessage(message);
    }

    private DtrBarState GetDtrBarState()
    {
        if (!Configuration.HideAllOtherPlayers)
        {
            return new DtrBarState(false, []);
        }

        var pauseReasonKeys = new List<string>();

        if (IsDutyCullingSuspended)
        {
            pauseReasonKeys.Add("Dtr.PauseReason.InDuty");
        }

        if (
            Configuration.DisableCullingBelowPlayerCount
            && ObjectTableStats.CurrentOtherPlayerCount() < Configuration.DisableCullingPlayerCountThreshold
        )
        {
            pauseReasonKeys.Add("Dtr.PauseReason.LowPlayerCount");
        }

        return new DtrBarState(true, pauseReasonKeys);
    }

    #endregion

    #region Localization

    private void OnLanguageChanged(string langCode)
    {
        Loc.Load(PluginInterface);
        RefreshDtrBar();
    }

    #endregion
}
