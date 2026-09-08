using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using EventHorizon.Application;
using EventHorizon.Culling;
using EventHorizon.Localization;
using EventHorizon.UI;

namespace EventHorizon.Features.Preview;

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
    internal static IFeatureDefinition CreateDefinition(
        ICullingReader reader,
        ICullingCommands commands,
        IGameGui gameGui,
        Action openSettings,
        Action<FeatureScope, string, Action> registerCommand
    ) =>
        new FeatureDefinition<PreviewSettings>(
            "preview",
            "Feature.Name.Preview",
            _ => false,
            (settings, save) => new PreviewFeature(settings, save, reader, commands, gameGui, openSettings, registerCommand)
        );

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
        var visible = ImGui.BeginChild("Preview", new Vector2(0, 800));
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

    private sealed class PlayerPreviewWindow : Window
    {
        private const float MinimumPreviewSide = 180f;
        private const float FloatingWindowDefaultSide = 300f;
        private const float GearIconOffsetX = 1.5f;

        private readonly PlayerPreviewPanel previewPanel;
        private readonly Action openMainWindow;
        private readonly Action close;

        public PlayerPreviewWindow(PlayerPreviewPanel previewPanel, Action openMainWindow, Action close)
            : base($"{Loc.Text("Config.Preview.FloatingTitle")}###EventHorizonPlayerPreview")
        {
            Size = new Vector2(FloatingWindowDefaultSide);
            SizeCondition = ImGuiCond.FirstUseEver;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(MinimumPreviewSide),
                MaximumSize = new Vector2(float.MaxValue),
            };

            this.previewPanel = previewPanel;
            this.openMainWindow = openMainWindow;
            this.close = close;

            TitleBarButtons.Add(
                new TitleBarButton
                {
                    Icon = FontAwesomeIcon.Cog,
                    IconOffset = new Vector2(GearIconOffsetX, 0f),
                    Click = _ => this.openMainWindow(),
                }
            );
        }

        public override void PreDraw()
        {
            WindowName = $"{Loc.Text("Config.Preview.FloatingTitle")}###EventHorizonPlayerPreview";
        }

        public override void OnClose() => close();

        public override void Draw()
        {
            previewPanel.DrawFloatingContent(PlayerKeepRuleLabels.GetLabel);
        }
    }
}

internal sealed class PreviewSettings : IFeatureSettings
{
    public int Version { get; set; } = 1;
}
