using System;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EventHorizon.Hooks;
using EventHorizon.Localization;
using EventHorizon.Rendering;
using EventHorizon.Windows;

namespace EventHorizon;

public sealed class Plugin : IDalamudPlugin
{
    private const int DynamicCullingRefreshIntervalMs = 200;
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
    internal static IPlayerState PlayerState { get; private set; } = null!;

    [PluginService]
    internal static IObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    internal static ITargetManager TargetManager { get; private set; } = null!;

    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    #endregion

    #region State

    public Configuration Configuration { get; init; }
    public int HiddenPlayerCount => UpdateObjectArraysHook.HiddenPlayerCount;

    public readonly WindowSystem WindowSystem = new("EventHorizon");
    private ConfigWindow ConfigWindow { get; init; }
    private UpdateObjectArraysHook UpdateObjectArraysHook { get; init; }
    private WorldOverlay WorldOverlay { get; init; }
    private long nextDynamicCullingRefresh;

    #endregion

    #region Lifecycle

    public Plugin()
    {
        Loc.Load(PluginInterface);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        ConfigWindow = new ConfigWindow(this, DataManager);
        UpdateObjectArraysHook = new UpdateObjectArraysHook(
            GameInteropProvider,
            Configuration,
            PlayerState,
            ObjectTable,
            TargetManager
        );
        WorldOverlay = new WorldOverlay(PluginInterface, Configuration, ObjectTable);

        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(
            PrimaryCommandName,
            new CommandInfo(OnCommand) { HelpMessage = Loc.Text("Command.Help.OpenSettings") }
        );
        CommandManager.AddHandler(
            ShortCommandName,
            new CommandInfo(OnCommand) { HelpMessage = Loc.Text("Command.Help.OpenSettings") }
        );

        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;
        PluginInterface.LanguageChanged += OnLanguageChanged;
        ChatGui.ChatMessage += OnChatMessage;
        Framework.Update += OnFrameworkUpdate;
        UpdateObjectArraysHook.Enable();

        Log.Information("Loaded.", PluginInterface.Manifest.Name);
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;
        PluginInterface.LanguageChanged -= OnLanguageChanged;
        ChatGui.ChatMessage -= OnChatMessage;
        Framework.Update -= OnFrameworkUpdate;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        UpdateObjectArraysHook.Dispose();
        WorldOverlay.Dispose();

        CommandManager.RemoveHandler(PrimaryCommandName);
        CommandManager.RemoveHandler(ShortCommandName);
    }

    #endregion

    #region UI

    private void OnCommand(string command, string args)
    {
        ToggleConfigUi();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();

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

        try
        {
            WorldOverlay.Draw();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WorldOverlay.Draw threw.");
        }
    }

    #endregion

    #region Culling

    public void RefreshObjectCulling(bool resetRuleState = false)
    {
        UpdateObjectArraysHook.Refresh(resetRuleState);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!NeedsDynamicCullingRefresh())
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now < nextDynamicCullingRefresh)
        {
            return;
        }

        nextDynamicCullingRefresh = now + DynamicCullingRefreshIntervalMs;
        RefreshObjectCulling();
    }

    private bool NeedsDynamicCullingRefresh()
    {
        return UpdateObjectArraysHook.NeedsDynamicRefresh;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        UpdateObjectArraysHook.RecordChatMessage(message);
    }

    #endregion

    #region Localization

    private void OnLanguageChanged(string langCode)
    {
        Loc.Load(PluginInterface);
    }

    #endregion
}
