using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
using EventHorizon.Preview;
using EventHorizon.Settings;
using EventHorizon.TargetingMarker;
using EventHorizon.UI.Config;
using EventHorizon.WorldGraphics;
using Pictomancy;
using PictomancyDemo;

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
    private DemoWindow PictomancyDemoWindow { get; init; }
    private PctContext? PictomancyContext { get; init; }
    private CullingController Culling { get; init; }
    private PlayerPreviewHighlighter PlayerPreviewHighlighter { get; init; }
    private ActorVfxController ActorVfxController { get; init; }
    private StaticVfxResourceRedirector StaticVfxResourceRedirector { get; init; }
    private StaticVfxController StaticVfxController { get; init; }
    private WorldDotOverlay WorldDotOverlay { get; init; }
    private SceneVisibilityController SceneVisibilityController { get; init; }
    private GBufferProbeController GBufferProbeController { get; init; }
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
        PlayerAdmissionDebugTrace.Initialize(PluginInterface, GameInteropProvider, ClientState, PlayerState, TargetManager);
        try
        {
            PlayerPreviewHighlighter = new PlayerPreviewHighlighter();
            ActorVfxController = new ActorVfxController(GameInteropProvider, SigScanner, Log);
            StaticVfxResourceRedirector = new StaticVfxResourceRedirector(PluginInterface, GameInteropProvider, Log);
            StaticVfxController = new StaticVfxController(GameInteropProvider, SigScanner, Log);
            WorldDotOverlay = new WorldDotOverlay(GameGui, PluginInterface);
            SceneVisibilityController = new SceneVisibilityController(GameInteropProvider, Configuration);
            GBufferProbeController = new GBufferProbeController(GameInteropProvider, Configuration, Log);
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
            PictomancyContext = InitializePictomancy();
            PictomancyDemoWindow = new DemoWindow();
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
            windowSystem.AddWindow(PictomancyDemoWindow);

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
            GBufferProbeController.Enable();
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
        PictomancyDemoWindow?.Dispose();
        PictomancyContext?.Dispose();
        SceneVisibilityController?.Dispose();
        GBufferProbeController?.Dispose();
        TargetingMarkerController?.Dispose();
        DtrBackground?.Dispose();
        DtrStatusBar?.Dispose();
        Culling?.Dispose();
        WorldDotOverlay?.Dispose();
        StaticVfxController?.Dispose();
        StaticVfxResourceRedirector?.Dispose();
        ActorVfxController?.Dispose();
        PlayerPreviewHighlighter?.Dispose();
        PlayerAdmissionDebugTrace.Close();
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
                PictomancyDemoWindow.Toggle();
                break;
            case "debugtarget":
                PlayerAdmissionDebugTrace.DumpCurrentTarget();
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

        DrawGBufferProbeWorldArrow();

        if (PictomancyContext is null || !PictomancyDemoWindow.IsOpen || !PictomancyDemoWindow.WorldDrawEnabled)
        {
            return;
        }

        try
        {
            PictomancyDemoWindow.DrawWorld();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Pictomancy demo DrawWorld threw.");
            PictomancyDemoWindow.WorldDrawEnabled = false;
        }
    }

    private void DrawGBufferProbeWorldArrow()
    {
        DrawGBufferDonorSampleMarker();

        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null || !GBufferProbeController.TryGetWorldTriangleMarkers(out var markers))
        {
            return;
        }

        foreach (var marker in markers)
        {
            PlayerPreviewWorldArrowRenderer.Draw(localPlayer.Position, marker.Center, GameGui, marker.Color, marker.Label);
        }
    }

    private void DrawGBufferDonorSampleMarker()
    {
        if (!Configuration.EnableGBufferProbe || Configuration.GBufferProbeMode != GBufferProbeMode.DonorOpaqueTuple)
        {
            return;
        }

        var displaySize = ImGui.GetIO().DisplaySize;
        var position = displaySize * GBufferProbeController.DonorSampleNormalized;
        var color = ImGui.GetColorU32(new Vector4(1f, 0.75f, 0.1f, 1f));
        var drawList = ImGui.GetBackgroundDrawList();
        drawList.AddCircle(position, 9f, color, 24, 2f);
        drawList.AddLine(position - new Vector2(14f, 0f), position + new Vector2(14f, 0f), color, 2f);
        drawList.AddLine(position - new Vector2(0f, 14f), position + new Vector2(0f, 14f), color, 2f);
        drawList.AddText(position + new Vector2(12f, 10f), color, "Donor sample");
    }

    private static PctContext? InitializePictomancy()
    {
        try
        {
            return PctService.Initialize(PluginInterface, new PctOptions { EnableKtkOutput = true });
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Pictomancy initialization failed; the 3D demo will run without world drawing.");
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
        DtrStatusBar.Update();
        PlayerPreviewHighlighter.Update();

        Culling.Update();
        PlayerAdmissionDebugTrace.Update(CullingStatus);
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
