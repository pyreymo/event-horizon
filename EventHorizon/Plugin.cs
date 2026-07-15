using System;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EventHorizon.Culling;
using EventHorizon.Dtr;
using EventHorizon.Integration.Debug;
using EventHorizon.Interop.Vfx;
using EventHorizon.Localization;
using EventHorizon.Preview;
using EventHorizon.Settings;
using EventHorizon.TargetingMarker;
using EventHorizon.UI.Config;
using EventHorizon.WorldGraphics;

namespace EventHorizon;

public sealed class Plugin : IDalamudPlugin
{
    private const string PrimaryCommandName = "/eventhorizon";
    private const string ShortCommandName = "/eh";

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

    [PluginService]
    internal static IKeyState KeyState { get; private set; } = null!;

    #endregion

    #region State

    internal Configuration Configuration { get; init; }

    private readonly WindowSystem windowSystem = new("EventHorizon");
    private ConfigWindow ConfigWindow { get; init; }
    private PlayerPreviewWindow PlayerPreviewWindow { get; init; }
    private CullingController Culling { get; init; }
    private PlayerPreviewHighlighter PlayerPreviewHighlighter { get; init; }
    private ActorVfxController ActorVfxController { get; init; }
    private StaticVfxResourceRedirector StaticVfxResourceRedirector { get; init; }
    private StaticVfxController StaticVfxController { get; init; }
    private WorldDotOverlay WorldDotOverlay { get; init; }
    private SceneVisibilityController SceneVisibilityController { get; init; }
    private DtrBar DtrStatusBar { get; init; }
    private DtrBackground DtrBackground { get; init; }
    private TargetingMarkerController TargetingMarkerController { get; init; }
    private bool disposed;

    public int HiddenPlayerCount => Culling.HiddenPlayerCount;
    internal CullingStatus CullingStatus => Culling.GetStatus();

    #endregion

    #region Lifecycle

    public Plugin()
    {
        Loc.Load(PluginInterface);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        DebugFileLog.Initialize(PluginInterface, Log);
        try
        {
            PlayerPreviewHighlighter = new PlayerPreviewHighlighter();
            ActorVfxController = new ActorVfxController(GameInteropProvider, SigScanner, Log);
            StaticVfxResourceRedirector = new StaticVfxResourceRedirector(PluginInterface, GameInteropProvider, Log);
            StaticVfxController = new StaticVfxController(GameInteropProvider, SigScanner, Log);
            WorldDotOverlay = new WorldDotOverlay(GameGui, PluginInterface);
            SceneVisibilityController = new SceneVisibilityController(GameInteropProvider, Configuration);
            Culling = new CullingController(
                GameInteropProvider,
                SigScanner,
                Configuration,
                PlayerState,
                Condition,
                ObjectTable,
                TargetManager,
                GameGui,
                StaticVfxController,
                WorldDotOverlay,
                Log
            );
            var playerPreviewPanel = new PlayerPreviewPanel(
                () => Culling.PlayerPreviewSnapshot,
                CullingRefreshPlayerPreview,
                SetPreviewSelectedPlayer,
                GameGui
            );
            ConfigWindow = new ConfigWindow(this, DataManager, playerPreviewPanel, IsPlayerPreviewWindowOpen, TogglePlayerPreviewWindow);
            PlayerPreviewWindow = new PlayerPreviewWindow(playerPreviewPanel, OpenMainUi);
            DtrStatusBar = new DtrBar(DtrBar, Configuration, Culling.GetStatus, SetPlayerHidingEnabled, ToggleConfigUi);
            DtrBackground = new DtrBackground(AddonLifecycle, GameGui, Framework, ClientState, Configuration);
            TargetingMarkerController = new TargetingMarkerController(
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
                WorldDotOverlay,
                Log
            );

            windowSystem.AddWindow(ConfigWindow);
            windowSystem.AddWindow(PlayerPreviewWindow);

            CommandManager.AddHandler(
                PrimaryCommandName,
                new CommandInfo(OnCommand) { HelpMessage = Loc.Text("Command.Help.OpenSettings") }
            );
            CommandManager.AddHandler(ShortCommandName, new CommandInfo(OnCommand) { HelpMessage = BuildCommandHelp(ShortCommandName) });

            PluginInterface.UiBuilder.Draw += OnDraw;
            PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
            PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
            PluginInterface.LanguageChanged += OnLanguageChanged;
            ChatGui.ChatMessage += OnChatMessage;
            Framework.Update += OnFrameworkUpdate;
            SceneVisibilityController.Enable();
            Culling.Enable();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.LanguageChanged -= OnLanguageChanged;
        ChatGui.ChatMessage -= OnChatMessage;
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(PrimaryCommandName);
        CommandManager.RemoveHandler(ShortCommandName);

        windowSystem.RemoveAllWindows();
        SceneVisibilityController?.Dispose();
        TargetingMarkerController?.Dispose();
        DtrBackground?.Dispose();
        DtrStatusBar?.Dispose();
        Culling?.Dispose();
        WorldDotOverlay?.Dispose();
        StaticVfxController?.Dispose();
        StaticVfxResourceRedirector?.Dispose();
        ActorVfxController?.Dispose();
        PlayerPreviewHighlighter?.Dispose();
        DebugFileLog.Close();
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

    public void RefreshDtrBar() => DtrStatusBar.RefreshNow();

    public void RefreshDtrBackground() => DtrBackground.Refresh();

    public void RequestTargetingMeMarkerRefresh() => TargetingMarkerController.RequestRefresh();

    private void OnDraw()
    {
        try
        {
            windowSystem.Draw();
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
        Culling.TemporarilyShowAllPlayers = IsTemporaryShowAllPlayersShortcutHeld();

        SceneVisibilityController.Update();
        DtrStatusBar.Update();
        PlayerPreviewHighlighter.Update();

        Culling.Update();
    }

    private bool IsTemporaryShowAllPlayersShortcutHeld()
    {
        if (!Configuration.EnableTemporaryShowAllPlayersShortcut)
        {
            return false;
        }

        var keys = Configuration.TemporarilyShowAllPlayersKeys;
        if (keys.Count == 0)
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (!KeyState.IsVirtualKeyValid(key) || !KeyState[key])
            {
                return false;
            }
        }

        return true;
    }

    public void RefreshObjectCulling(bool resetRuleState = false)
    {
        Culling.Refresh(resetRuleState);
    }

    private void CullingRefreshPlayerPreview() => Culling.RefreshPlayerPreview();

    internal void SetPreviewSelectedPlayer(uint? entityId)
    {
        PlayerPreviewHighlighter.SetSelectedPlayer(entityId);
        if (Culling.SetPreviewSelectedPlayer(entityId))
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
        Culling.RecordChatMessage(message);
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
