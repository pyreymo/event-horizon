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
    private float lastWindowSide = FloatingWindowDefaultSide;

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
        SnapWindowSizeToSquare();
    }

    public override void Draw()
    {
        previewPanel.DrawFloatingContent(PlayerKeepRuleLabels.GetLabel);
    }

    private void SnapWindowSizeToSquare()
    {
        if (!Size.HasValue)
        {
            return;
        }

        var size = Size.Value;
        if (Math.Abs(size.X - size.Y) <= 0.5f)
        {
            lastWindowSide = size.X;
            SizeCondition = ImGuiCond.FirstUseEver;
            return;
        }

        var side = Math.Abs(size.X - lastWindowSide) >= Math.Abs(size.Y - lastWindowSide) ? size.X : size.Y;
        side = Math.Max(MinimumPreviewSide, side);
        lastWindowSide = side;
        Size = new Vector2(side);
        SizeCondition = ImGuiCond.Always;
    }
}
