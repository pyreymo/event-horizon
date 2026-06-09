using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using EventHorizon.Localization;

namespace EventHorizon.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin)
        : base($"{Loc.Text("Config.Title")}###EventHorizonConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(360, 120);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        WindowName = $"{Loc.Text("Config.Title")}###EventHorizonConfig";
    }

    public override void Draw()
    {
        var enabled = configuration.Enabled;
        if (ImGui.Checkbox(Loc.Text("Config.Enabled"), ref enabled))
        {
            configuration.Enabled = enabled;
            configuration.Save();
            plugin.RefreshObjectCulling();
        }

        var hideAllOtherPlayers = configuration.HideAllOtherPlayers;
        if (ImGui.Checkbox(Loc.Text("Config.HideAllOtherPlayers"), ref hideAllOtherPlayers))
        {
            configuration.HideAllOtherPlayers = hideAllOtherPlayers;
            configuration.Save();
            plugin.RefreshObjectCulling();
        }

        ImGui.TextUnformatted(Loc.Text("Config.CullingRulesPlaceholder"));
    }
}
