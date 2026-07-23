using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EventHorizon.Playground3D;

internal sealed class DemoWindow : Window
{
    public DemoWindow()
        : base("Rendering Research")
    {
        IsOpen = false;
        SizeCondition = ImGuiCond.FirstUseEver;
        Size = new Vector2(560, 480);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 240),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public override void Draw() { }
}
