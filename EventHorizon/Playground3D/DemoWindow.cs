using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Underpaint;

namespace EventHorizon.Playground3D;

internal sealed unsafe class DemoWindow : Window
{
    private const ulong TriangleId = 1;
    private const int MainView = 30;
    private const int MainRenderCameraSubView = 12;

    private readonly Renderer? renderer;
    private Matrix4x4 triangleWorld;
    private bool hasTriangleWorld;

    public DemoWindow(Renderer? renderer)
        : base("Rendering Research")
    {
        this.renderer = renderer;
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

    public void SubmitFrame()
    {
        if (renderer == null)
            return;

        if (!hasTriangleWorld)
        {
            var manager = Manager.Instance();
            var camera = manager == null ? null : manager->Views[MainView].SubViews[MainRenderCameraSubView].Camera;
            if (camera == null)
                return;

            var view = (Matrix4x4)camera->ViewMatrix;
            view.M44 = 1;
            if (!Matrix4x4.Invert(view, out var inverseView))
                return;

            triangleWorld = Matrix4x4.CreateTranslation(0, 0, -5) * inverseView;
            hasTriangleWorld = true;
        }

        renderer.SubmitTriangle(TriangleId, triangleWorld, triangleWorld, new Vector3(1, 0, 0), 0.5f);
    }

    public override void Draw() { }
}
