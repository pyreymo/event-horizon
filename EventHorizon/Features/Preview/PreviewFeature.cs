using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using EventHorizon.Application;
using EventHorizon.Culling;
using EventHorizon.Localization;
using EventHorizon.UI;

namespace EventHorizon.Features.Preview;

internal sealed class PreviewSettings
{
    public int Version { get; set; } = 1;
}

internal sealed class PreviewFeature(
    PreviewSettings settings,
    Action save,
    ICullingReader reader,
    ICullingCommands commands,
    IGameGui gameGui,
    Action openSettings,
    Action<FeatureScope, string, Action> registerCommand
) : Feature<PreviewSettings>(settings, save)
{
    private PlayerPreviewPanel? panel;
    private PlayerPreviewWindow? window;
    private PlayerPreviewHighlighter? highlighter;
    private IPlayerReveal? reveal;
    private PlayerHandle? selected;
    private long lastSelection;

    public override void Enable(FeatureScope scope)
    {
        var preview = new PlayerPreview(reader);
        highlighter = scope.Own(new PlayerPreviewHighlighter());
        scope.Defer(ClearSelection);
        panel = new PlayerPreviewPanel(() => preview.Snapshot, preview.Refresh, Select, gameGui);
        window = new PlayerPreviewWindow(panel, openSettings, ClearSelection);
        var windows = new WindowSystem("EventHorizon.Preview");
        var activeWindow = window;
        scope.Defer(() =>
        {
            activeWindow.IsOpen = false;
            windows.RemoveAllWindows();
        });
        windows.AddWindow(activeWindow);
        scope.OnDraw(windows.Draw);
        scope.OnUpdate(() =>
        {
            if (Environment.TickCount64 - lastSelection > 500)
                ClearSelection();
            highlighter?.Update();
        });
        registerCommand(scope, "preview", activeWindow.Toggle);
    }

    private void Select(PlayerHandle? handle)
    {
        if (handle != selected)
        {
            ClearSelection();
            selected = handle;
            if (handle.HasValue)
                reveal = commands.AcquireReveal(handle.Value);
        }
        lastSelection = Environment.TickCount64;
        reveal?.Renew();
        highlighter?.SetSelectedPlayer(handle);
    }

    private void ClearSelection()
    {
        reveal?.Dispose();
        reveal = null;
        selected = null;
        highlighter?.SetSelectedPlayer(null);
    }

    public override void Disable()
    {
        ClearSelection();
        panel = null;
        window = null;
        // The activation scope owns and clears the highlighter.
    }

    public override void DrawSettings()
    {
        ImGui.TextWrapped(Loc.Text("Feature.Preview.AdmissionNote"));
        if (panel == null || window == null)
        {
            ImGui.TextDisabled(Loc.Text("Feature.EnableFirst"));
            return;
        }
        if (ImGui.Button(Loc.Text("Feature.Preview.Open")))
            window.Toggle();
        var visible = ImGui.BeginChild("Preview", new Vector2(0, 400));
        try
        {
            if (visible)
                panel.DrawInlineContent(PlayerKeepRuleLabels.GetLabel);
        }
        finally
        {
            ImGui.EndChild();
        }
    }
}
