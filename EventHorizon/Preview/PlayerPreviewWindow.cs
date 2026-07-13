using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using EventHorizon.Localization;
using EventHorizon.UI;

namespace EventHorizon.Preview;

internal sealed class PlayerPreviewWindow : Window
{
    private const float MinimumPreviewSide = 180f;
    private const float FloatingWindowDefaultSide = 300f;
    private const float GearIconOffsetX = 1.5f;

    private readonly PlayerPreviewPanel previewPanel;
    private readonly Action openMainWindow;

    public PlayerPreviewWindow(PlayerPreviewPanel previewPanel, Action openMainWindow)
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

    public override void Draw()
    {
        previewPanel.DrawFloatingContent(PlayerKeepRuleLabels.GetLabel);
    }
}
