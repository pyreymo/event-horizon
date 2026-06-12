using System;
using System.Collections.Generic;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EventHorizon.Hooks;
using EventHorizon.Integration;
using EventHorizon.Localization;
using EventHorizon.ObjectTable;
using EventHorizon.Rendering;
using EventHorizon.Windows;

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
    internal static ICondition Condition { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IDtrBar DtrBar { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    #endregion

    #region State

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("EventHorizon");
    private ConfigWindow ConfigWindow { get; init; }
    private UpdateObjectArraysHook UpdateObjectArraysHook { get; init; }
    private WorldOverlay WorldOverlay { get; init; }
    private DtrBarIntegration DtrBarIntegration { get; init; }

    private long nextDynamicCullingRefresh;
    private long nextDtrBarRefresh;
    public int HiddenPlayerCount => UpdateObjectArraysHook.HiddenPlayerCount;
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
        ConfigWindow = new ConfigWindow(this, DataManager);
        UpdateObjectArraysHook = new UpdateObjectArraysHook(
            GameInteropProvider,
            Configuration,
            PlayerState,
            Condition,
            ObjectTable,
            TargetManager
        );
        WorldOverlay = new WorldOverlay(PluginInterface, Configuration, ObjectTable);
        DtrBarIntegration = new DtrBarIntegration(
            DtrBar,
            Configuration,
            GetDtrBarState,
            SetPlayerHidingEnabled,
            ToggleConfigUi
        );

        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(
            PrimaryCommandName,
            new CommandInfo(OnCommand) { HelpMessage = Loc.Text("Command.Help.OpenSettings") }
        );
        CommandManager.AddHandler(
            ShortCommandName,
            new CommandInfo(OnCommand) { HelpMessage = BuildCommandHelp(ShortCommandName) }
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
        DtrBarIntegration.Dispose();
        UpdateObjectArraysHook.Dispose();
        WorldOverlay.Dispose();

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

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    public void RefreshDtrBar() => DtrBarIntegration.Refresh();

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

    private void OnFrameworkUpdate(IFramework framework)
    {
        RefreshDtrBarIfNeeded();

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

    private void RefreshDtrBarIfNeeded()
    {
        var now = Environment.TickCount64;
        if (now < nextDtrBarRefresh)
        {
            return;
        }

        nextDtrBarRefresh = now + DtrBarRefreshIntervalMs;
        RefreshDtrBar();
    }

    private bool NeedsDynamicCullingRefresh()
    {
        return UpdateObjectArraysHook.NeedsDynamicRefresh;
    }

    public void RefreshObjectCulling(bool resetRuleState = false)
    {
        UpdateObjectArraysHook.Refresh(resetRuleState);
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
            && ObjectTableStats.CurrentPlayerCount()
                < Configuration.DisableCullingPlayerCountThreshold
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
