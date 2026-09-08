using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using EventHorizon.Localization;
using EventHorizon.UI;

namespace EventHorizon.Features.Preview;

internal sealed class PlayerPreviewWindow : Window
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
