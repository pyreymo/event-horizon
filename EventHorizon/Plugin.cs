using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using EventHorizon.Rendering;
using EventHorizon.Settings;
using EventHorizon.UI.Config;

namespace EventHorizon;

public sealed class Plugin : IDalamudPlugin
{
    private const string PrimaryCommandName = "/eventhorizon";
    private const string ShortCommandName = "/eh";
    private const int DynamicCullingRefreshIntervalMs = 200;
    private const int DtrBarRefreshIntervalMs = 1_000;
    private const double SlowFrameworkUpdateLogThresholdMs = 2.0;
    private const int SlowFrameworkUpdateLogCooldownMs = 1_000;

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
    private CharacterAlphaController CharacterAlphaController { get; init; }

    private long nextDynamicCullingRefresh;
    private long nextDtrBarRefresh;
    private long nextSlowFrameworkUpdateLog;
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
            StaticVfxController
        );
        CharacterAlphaController = new CharacterAlphaController(ObjectTable);
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

        Log.Information("Loaded.", PluginInterface.Manifest.Name);
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
        CharacterAlphaController.Dispose();
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
        var start = Stopwatch.GetTimestamp();
        var phaseStart = start;
        RefreshDtrBarIfNeeded();
        var dtrTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        LayoutGraphicsVisibilityController.Update(Configuration.HideBgPartGraphicsObjects, Configuration.HideTerrainGraphicsObjects);
        var layoutGraphicsTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        PlayerPreviewHighlighter.Update();
        var highlightTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        if (!NeedsDynamicCullingRefresh())
        {
            var needsDynamicTicks = Stopwatch.GetTimestamp() - phaseStart;
            LogSlowFrameworkUpdate(
                start,
                dtrTicks,
                layoutGraphicsTicks,
                highlightTicks,
                needsDynamicTicks,
                0,
                0,
                didRefresh: false,
                tickTrace: default
            );
            return;
        }
        var dynamicCheckTicks = Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        UpdateObjectArraysHook.Tick();
        var tickTicks = Stopwatch.GetTimestamp() - phaseStart;
        var tickTrace = UpdateObjectArraysHook.LastTickTrace;

        var now = Environment.TickCount64;
        if (now < nextDynamicCullingRefresh)
        {
            LogSlowFrameworkUpdate(
                start,
                dtrTicks,
                layoutGraphicsTicks,
                highlightTicks,
                dynamicCheckTicks,
                tickTicks,
                0,
                didRefresh: false,
                tickTrace
            );
            return;
        }

        phaseStart = Stopwatch.GetTimestamp();
        RefreshObjectCulling();
        var refreshTicks = Stopwatch.GetTimestamp() - phaseStart;
        nextDynamicCullingRefresh = Environment.TickCount64 + DynamicCullingRefreshIntervalMs;
        LogSlowFrameworkUpdate(
            start,
            dtrTicks,
            layoutGraphicsTicks,
            highlightTicks,
            dynamicCheckTicks,
            tickTicks,
            refreshTicks,
            didRefresh: true,
            tickTrace
        );
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

    private bool NeedsDynamicCullingRefresh()
    {
        return UpdateObjectArraysHook.NeedsDynamicRefresh;
    }

    private void LogSlowFrameworkUpdate(
        long start,
        long dtrTicks,
        long layoutGraphicsTicks,
        long highlightTicks,
        long dynamicCheckTicks,
        long tickTicks,
        long refreshTicks,
        bool didRefresh,
        CullingPerformanceTrace tickTrace
    )
    {
        var totalTicks = Stopwatch.GetTimestamp() - start;
        if (ToMilliseconds(totalTicks) < SlowFrameworkUpdateLogThresholdMs)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now < nextSlowFrameworkUpdateLog)
        {
            return;
        }

        nextSlowFrameworkUpdateLog = now + SlowFrameworkUpdateLogCooldownMs;
        Log.Information(
            "[Perf] Slow Plugin.OnFrameworkUpdate total={TotalMs:F3}ms dtr={DtrMs:F3}ms layoutGraphics={LayoutGraphicsMs:F3}ms highlight={HighlightMs:F3}ms dynamicCheck={DynamicCheckMs:F3}ms tick={TickMs:F3}ms refresh={RefreshMs:F3}ms didRefresh={DidRefresh} tickTrace={TickTrace} refreshTrace={RefreshTrace}",
            ToMilliseconds(totalTicks),
            ToMilliseconds(dtrTicks),
            ToMilliseconds(layoutGraphicsTicks),
            ToMilliseconds(highlightTicks),
            ToMilliseconds(dynamicCheckTicks),
            ToMilliseconds(tickTicks),
            ToMilliseconds(refreshTicks),
            didRefresh,
            FormatCullingTrace(tickTrace),
            didRefresh ? FormatCullingTrace(UpdateObjectArraysHook.LastRefreshTrace) : "n/a"
        );
    }

    private static string FormatCullingTrace(CullingPerformanceTrace trace)
    {
        if (!trace.HasValue)
        {
            return "n/a";
        }

        return string.Format(
            "total={0:F3} guard={1:F3} keep={2:F3} plan={3:F3} reconcile={4:F3} preview={5:F3} previewTrace[{6}] actions={7} pendingShow={8} pendingHide={9} previewActive={10} tick[{11}]",
            ToMilliseconds(trace.TotalTicks),
            ToMilliseconds(trace.GuardTicks),
            ToMilliseconds(trace.KeepPlanTicks),
            ToMilliseconds(trace.VisibilityPlanTicks),
            ToMilliseconds(trace.ReconcileTicks),
            ToMilliseconds(trace.PreviewTicks),
            FormatPreviewTrace(trace.Preview),
            trace.ActionCount,
            trace.PendingShowCount,
            trace.PendingHideCount,
            trace.RefreshPlayerPreview,
            FormatTickTrace(trace.Tick)
        );
    }

    private static string FormatPreviewTrace(CullingPreviewPerformanceTrace trace)
    {
        if (!trace.HasValue)
        {
            return "n/a";
        }

        return string.Format(
            "begin={0:F3} add={1:F3} build={2:F3} entries={3}",
            ToMilliseconds(trace.BeginTicks),
            ToMilliseconds(trace.AddTicks),
            ToMilliseconds(trace.BuildTicks),
            trace.EntryCount
        );
    }

    private static string FormatTickTrace(CullingTickPerformanceTrace trace)
    {
        if (!trace.HasValue)
        {
            return "n/a";
        }

        var accountedTicks =
            trace.PlayerActionsTicks + trace.NonPlayerTicks + trace.PruneHiddenTicks + trace.PruneFadesTicks + trace.HiddenVfxTicks;
        var unaccountedTicks = Math.Max(0, trace.TotalTicks - accountedTicks);
        return string.Format(
            "total={0:F3} playerActions={1:F3} nonPlayer={2:F3} pruneHidden={3:F3} pruneFades={4:F3} hiddenVfx={5:F3} hiddenVfxTrace[{6}] unaccounted={7:F3} actions={8}",
            ToMilliseconds(trace.TotalTicks),
            ToMilliseconds(trace.PlayerActionsTicks),
            ToMilliseconds(trace.NonPlayerTicks),
            ToMilliseconds(trace.PruneHiddenTicks),
            ToMilliseconds(trace.PruneFadesTicks),
            ToMilliseconds(trace.HiddenVfxTicks),
            FormatHiddenVfxTrace(trace.HiddenVfx),
            ToMilliseconds(unaccountedTicks),
            trace.ActionCount
        );
    }

    private static string FormatHiddenVfxTrace(CullingHiddenVfxPerformanceTrace trace)
    {
        if (!trace.HasValue)
        {
            return "n/a";
        }

        return string.Format(
            "collect={0:F3} project={1:F3} show={2:F3} prune={3:F3} clear={4:F3} hidden={5} visible={6} active={7} created={8} updated={9} skipped={10} removed={11} deferred={12}",
            ToMilliseconds(trace.CollectTicks),
            ToMilliseconds(trace.ProjectTicks),
            ToMilliseconds(trace.ShowTicks),
            ToMilliseconds(trace.PruneTicks),
            ToMilliseconds(trace.ClearTicks),
            trace.HiddenCount,
            trace.VisibleCount,
            trace.ActiveCount,
            trace.ShowCreatedCount,
            trace.ShowUpdatedCount,
            trace.ShowSkippedCount,
            trace.ShowRemovedCount,
            trace.ShowDeferredCount
        );
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
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
