using System.IO;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EventHorizon.Culling;
using EventHorizon.Dtr;
using EventHorizon.Integration.Debug;
using EventHorizon.Interop.Vfx;
using EventHorizon.Localization;
using EventHorizon.Playground3D;
using EventHorizon.Preview;
using EventHorizon.Settings;
using EventHorizon.TargetingMarker;
using EventHorizon.UI.Config;
using EventHorizon.WorldGraphics;
using Underpaint;

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
    internal static INotificationManager NotificationManager { get; private set; } = null!;

    [PluginService]
    internal static IKeyState KeyState { get; private set; } = null!;

    #endregion

    #region State

    internal Configuration Configuration { get; init; }

    private readonly WindowSystem windowSystem = new("EventHorizon");
    private ConfigWindow ConfigWindow { get; init; }
    private PlayerPreviewWindow PlayerPreviewWindow { get; init; }
    private DemoWindow UnderpaintDemoWindow { get; init; }
    private UnderpaintRenderer? UnderpaintRenderer { get; init; }
    private CullingController Culling { get; init; }
    private PlayerPreviewHighlighter PlayerPreviewHighlighter { get; init; }
    private ActorVfxController ActorVfxController { get; init; }
    private StaticVfxResourceRedirector StaticVfxResourceRedirector { get; init; }
    private StaticVfxController StaticVfxController { get; init; }
    private WorldDotOverlay WorldDotOverlay { get; init; }
    private SceneVisibilityController SceneVisibilityController { get; init; }
    private GBufferProbeController GBufferProbeController { get; init; }
#if DEBUG
    private NativeOpaquePreviewController NativeOpaquePreview { get; init; }
#endif
    private DtrBar DtrStatusBar { get; init; }
    private DtrBackground DtrBackground { get; init; }
    private TargetingMarkerController TargetingMarkerController { get; init; }
    private bool disposed;

    public int HiddenPlayerCount => Culling.HiddenPlayerCount;
    internal CullingStatus CullingStatus => Culling.GetStatus();
    internal GBufferProbeController GBufferProbe => GBufferProbeController;

    #endregion

    #region Lifecycle

    public Plugin()
    {
        Loc.Load(PluginInterface);

        var configurationLoadFailed = false;
        try
        {
            Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to load configuration. Using safe defaults.");
            Configuration = Configuration.CreateSafeDefault();
            configurationLoadFailed = true;
        }

        var configurationNormalized = Configuration.Normalize(KeyState.IsVirtualKeyValid);
        DebugFileLog.Initialize(PluginInterface, Log);
        try
        {
            PlayerPreviewHighlighter = new PlayerPreviewHighlighter();
            ActorVfxController = new ActorVfxController(GameInteropProvider, SigScanner, Log);
            StaticVfxResourceRedirector = new StaticVfxResourceRedirector(PluginInterface, GameInteropProvider, Log);
            StaticVfxController = new StaticVfxController(GameInteropProvider, SigScanner, Log);
            WorldDotOverlay = new WorldDotOverlay(GameGui, PluginInterface);
            SceneVisibilityController = new SceneVisibilityController(GameInteropProvider, Configuration);
            UnderpaintRenderer = InitializeUnderpaint();
#if DEBUG
            NativeOpaquePreview = new NativeOpaquePreviewController(UnderpaintRenderer);
#endif
            GBufferProbeController = new GBufferProbeController(Configuration, UnderpaintRenderer);
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
            UnderpaintDemoWindow = new DemoWindow(UnderpaintRenderer, ObjectTable, TargetManager, TextureProvider);
#if DEBUG
            UnderpaintDemoWindow.AttachNativeOpaquePreview(NativeOpaquePreview);
#endif
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
            windowSystem.AddWindow(UnderpaintDemoWindow);

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

            PersistLoadedConfiguration(configurationLoadFailed || configurationNormalized);
            if (configurationLoadFailed)
            {
                NotificationManager.AddNotification(
                    new Notification
                    {
                        Title = Loc.Text("Notification.ConfigLoadFailed.Title"),
                        Content = Loc.Text("Notification.ConfigLoadFailed.Content"),
                        Type = NotificationType.Warning,
                    }
                );
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void PersistLoadedConfiguration(bool backupOriginal)
    {
        if (backupOriginal && !TryBackupConfiguration())
        {
            return;
        }

        Configuration.Save();
    }

    private static bool TryBackupConfiguration()
    {
        var configFile = PluginInterface.ConfigFile;
        if (!configFile.Exists)
        {
            return true;
        }

        try
        {
            File.Copy(configFile.FullName, $"{configFile.FullName}.bak", true);
            return true;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to back up configuration. The original file was not overwritten.");
            return false;
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
        UnderpaintDemoWindow?.Dispose();
#if DEBUG
        NativeOpaquePreview?.Dispose();
#endif
        SceneVisibilityController?.Dispose();
        GBufferProbeController?.Dispose();
        UnderpaintRenderer?.Dispose();
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
            case "3d":
                UnderpaintDemoWindow.Toggle();
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
            Loc.Text("Command.Help.Preview"),
            Loc.Text("Command.Help.3D")
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

        var demoOwnsOpaqueBackend =
            UnderpaintDemoWindow.IsOpen && UnderpaintDemoWindow.WorldDrawEnabled && UnderpaintDemoWindow.UsesOpaqueBackend;
        GBufferProbeController.PublishingEnabled = !demoOwnsOpaqueBackend;

        DrawGBufferProbeWorldArrow();

        if (UnderpaintRenderer is null || !UnderpaintDemoWindow.IsOpen || !UnderpaintDemoWindow.WorldDrawEnabled)
        {
            UnderpaintDemoWindow.StopWorldDraw();
            return;
        }

        try
        {
            UnderpaintDemoWindow.DrawWorld();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Underpaint demo DrawWorld threw.");
            UnderpaintDemoWindow.WorldDrawEnabled = false;
        }
    }

    private void DrawGBufferProbeWorldArrow()
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null || !GBufferProbeController.TryGetWorldMarker(out var marker))
        {
            return;
        }

        PlayerPreviewWorldArrowRenderer.Draw(localPlayer.Position, marker.Center, GameGui, marker.Color, marker.Label);
    }

    private static UnderpaintRenderer? InitializeUnderpaint()
    {
        try
        {
            return new UnderpaintRenderer(GameInteropProvider, Log);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Underpaint initialization failed; G-buffer drawing is unavailable.");
            return null;
        }
    }

    #endregion

    #region Culling

    private void OnFrameworkUpdate(IFramework framework)
    {
        Culling.TemporarilyShowAllPlayers = IsTemporaryShowAllPlayersShortcutHeld();

        SceneVisibilityController.Update();
        GBufferProbeController.Update();
#if DEBUG
        NativeOpaquePreview.Update();
#endif
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
