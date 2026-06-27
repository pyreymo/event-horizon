using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using EventHorizon.Localization;
using EventHorizon.ObjectTable;

namespace EventHorizon.Windows;

internal sealed class PlayerPreviewWindow : Window, IDisposable
{
    private readonly PlayerPreviewPanel previewPanel;
    private readonly Action openMainWindow;
    private float lastWindowSide = PlayerPreviewConstants.FloatingWindowDefaultSide;

    public PlayerPreviewWindow(PlayerPreviewPanel previewPanel, Action openMainWindow)
        : base($"{Loc.Text("Config.Preview.FloatingTitle")}###EventHorizonPlayerPreview")
    {
        Size = new Vector2(PlayerPreviewConstants.FloatingWindowDefaultSide);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(PlayerPreviewConstants.MinimumPreviewSide),
            MaximumSize = new Vector2(float.MaxValue),
        };

        this.previewPanel = previewPanel;
        this.openMainWindow = openMainWindow;

        TitleBarButtons.Add(
            new TitleBarButton
            {
                Icon = FontAwesomeIcon.Cog,
                IconOffset = new Vector2(PlayerPreviewConstants.FloatingWindowGearIconOffsetX, 0f),
                Click = _ => this.openMainWindow(),
            }
        );
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        WindowName = $"{Loc.Text("Config.Preview.FloatingTitle")}###EventHorizonPlayerPreview";
        SnapWindowSizeToSquare();
    }

    public override void Draw()
    {
        previewPanel.DrawFloatingContent(PlayerKeepRuleText.GetLabel);
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
        side = Math.Max(PlayerPreviewConstants.MinimumPreviewSide, side);
        lastWindowSide = side;
        Size = new Vector2(side);
        SizeCondition = ImGuiCond.Always;
    }
}
