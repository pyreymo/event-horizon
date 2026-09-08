using System;
using System.IO;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EventHorizon.Application;
using EventHorizon.Culling;
using EventHorizon.Features;
using EventHorizon.Localization;
using EventHorizon.Settings;
using EventHorizon.UI.Config;

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
    internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal static INotificationManager NotificationManager { get; private set; } = null!;

    [PluginService]
    internal static IKeyState KeyState { get; private set; } = null!;

    [PluginService]
    internal static IDtrBar DtrBar { get; private set; } = null!;

    [PluginService]
    internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService]
    internal static INamePlateGui NamePlateGui { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static ITextureProvider TextureProvider { get; private set; } = null!;

    #endregion

    #region State

    internal Configuration Configuration { get; init; }

    private readonly WindowSystem windowSystem = new("EventHorizon");
    private ConfigWindow ConfigWindow { get; init; } = null!;
    internal CullingController Culling { get; private set; } = null!;
    private bool disposed;
    internal FeatureHost Features { get; private set; } = null!;

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
        try
        {
            Culling = new CullingController(
                GameInteropProvider,
                SigScanner,
                Configuration,
                PlayerState,
                Condition,
                ObjectTable,
                TargetManager,
                GameGui,
                Log
            );
            ConfigWindow = new ConfigWindow(this, DataManager);
            windowSystem.AddWindow(ConfigWindow);

            CommandManager.AddHandler(
                PrimaryCommandName,
                new CommandInfo(OnCommand) { HelpMessage = BuildCommandHelp(PrimaryCommandName) }
            );
            CommandManager.AddHandler(ShortCommandName, new CommandInfo(OnCommand) { HelpMessage = BuildCommandHelp(ShortCommandName) });

            PluginInterface.UiBuilder.Draw += OnDraw;
            PluginInterface.UiBuilder.OpenConfigUi += OpenUi;
            PluginInterface.UiBuilder.OpenMainUi += OpenUi;
            PluginInterface.LanguageChanged += OnLanguageChanged;
            ChatGui.ChatMessage += OnChatMessage;
            Framework.Update += OnFrameworkUpdate;
            Culling.Enable();
            var featureStore = new FeatureConfigStore(PluginInterface, Log);
            var api = new CullingApi(Culling, Configuration, SetPlayerHidingEnabled);
            Features = new FeatureHost(
                FeatureCatalog.Create(
                    featureStore,
                    api,
                    api,
                    OpenUi,
                    (scope, command, action) => Features.RegisterCommand(scope, command, action),
                    configurationLoadFailed
                ),
                featureStore,
                Framework,
                PluginInterface,
                Log,
                configurationLoadFailed
            );

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
        PluginInterface.UiBuilder.OpenConfigUi -= OpenUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenUi;
        PluginInterface.LanguageChanged -= OnLanguageChanged;
        ChatGui.ChatMessage -= OnChatMessage;
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(PrimaryCommandName);
        CommandManager.RemoveHandler(ShortCommandName);

        windowSystem.RemoveAllWindows();
        try
        {
            Features?.Dispose();
        }
        finally
        {
            Culling?.Dispose();
        }
    }

    #endregion

    #region Commands

    private void OnCommand(string command, string args)
    {
        if (Features?.TryCommand(args.Trim()) == true)
            return;
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
            Loc.Text("Command.Help.Toggle")
        );
    }

    private void ToggleConfigUi() => ConfigWindow.Toggle();

    private void OpenUi() => ConfigWindow.Open();

    private void OnDraw()
    {
        try
        {
            windowSystem.Draw();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "WindowSystem.Draw threw.");
        }
    }

    #endregion

    #region Culling

    private void OnFrameworkUpdate(IFramework framework)
    {
        Culling.TemporarilyShowAllPlayers = IsTemporaryShowAllPlayersShortcutHeld();
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

    private void SetPlayerHidingEnabled(bool enabled)
    {
        if (Configuration.HideAllOtherPlayers == enabled)
            return;

        Configuration.HideAllOtherPlayers = enabled;
        Configuration.Save();
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
    }

    #endregion
}
