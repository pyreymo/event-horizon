using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EventHorizon.Hooks;
using EventHorizon.Localization;
using EventHorizon.Windows;

namespace EventHorizon;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    [PluginService]
    internal static IPlayerState PlayerState { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    private const string PrimaryCommandName = "/eventhorizon";
    private const string ShortCommandName = "/eh";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("EventHorizon");
    private ConfigWindow ConfigWindow { get; init; }
    private UpdateObjectArraysHook UpdateObjectArraysHook { get; init; }

    public Plugin()
    {
        Loc.Load(PluginInterface);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        ConfigWindow = new ConfigWindow(this);
        UpdateObjectArraysHook = new UpdateObjectArraysHook(
            GameInteropProvider,
            Configuration,
            PlayerState
        );

        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(
            PrimaryCommandName,
            new CommandInfo(OnCommand) { HelpMessage = Loc.Text("Command.Help.OpenSettings") }
        );
        CommandManager.AddHandler(
            ShortCommandName,
            new CommandInfo(OnCommand) { HelpMessage = Loc.Text("Command.Help.OpenSettings") }
        );

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;
        PluginInterface.LanguageChanged += OnLanguageChanged;
        UpdateObjectArraysHook.Enable();

        Log.Information("{Name} loaded.", PluginInterface.Manifest.Name);
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;
        PluginInterface.LanguageChanged -= OnLanguageChanged;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        UpdateObjectArraysHook.Dispose();

        CommandManager.RemoveHandler(PrimaryCommandName);
        CommandManager.RemoveHandler(ShortCommandName);
    }

    private void OnCommand(string command, string args)
    {
        ToggleConfigUi();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    public void RefreshObjectCulling()
    {
        UpdateObjectArraysHook.Refresh();
    }

    private void OnLanguageChanged(string langCode)
    {
        Loc.Load(PluginInterface);
    }
}
