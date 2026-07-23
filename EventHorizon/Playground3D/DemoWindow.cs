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
    private readonly Triangle[] frame = new Triangle[2];
    private Matrix4x4 anchorWorld;
    private Matrix4x4 currentWorld;
    private Matrix4x4 secondCurrentWorld;
    private bool hasAnchorWorld;
    private Vector3 offset;
    private float rotationDegrees;
    private float scale = 1;
    private Vector3 color = new(1, 0, 0);
    private float alpha = 0.5f;
    private float ditherFade = 1;

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

        if (!hasAnchorWorld)
        {
            var manager = Manager.Instance();
            var camera = manager == null ? null : manager->Views[MainView].SubViews[MainRenderCameraSubView].Camera;
            if (camera == null)
                return;

            var view = (Matrix4x4)camera->ViewMatrix;
            view.M44 = 1;
            if (!Matrix4x4.Invert(view, out var inverseView))
                return;

            anchorWorld = Matrix4x4.CreateTranslation(0, 0, -5) * inverseView;
            currentWorld = anchorWorld;
            secondCurrentWorld = Matrix4x4.CreateTranslation(1.5f, 0, 0) * anchorWorld;
            hasAnchorWorld = true;
        }

        var previousWorld = currentWorld;
        var secondPreviousWorld = secondCurrentWorld;
        currentWorld =
            Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateRotationY(rotationDegrees * MathF.PI / 180)
            * Matrix4x4.CreateTranslation(offset)
            * anchorWorld;
        secondCurrentWorld = Matrix4x4.CreateScale(0.75f) * Matrix4x4.CreateTranslation(offset + new Vector3(1.5f, 0, 0)) * anchorWorld;

        frame[0] = new Triangle(TriangleId, currentWorld, previousWorld, color, alpha, ditherFade);
        frame[1] = new Triangle(2, secondCurrentWorld, secondPreviousWorld, new Vector3(0, 1, 0), 0.75f, 1);
        renderer.SubmitFrame(frame);
    }

    public override void Draw()
    {
        ImGui.DragFloat3("Offset", ref offset, 0.05f);
        ImGui.SliderFloat("Rotation", ref rotationDegrees, -180, 180, "%.0f deg");
        ImGui.SliderFloat("Scale", ref scale, 0.1f, 5, "%.2f");
        ImGui.ColorEdit3("Color", ref color);
        ImGui.SliderFloat("Alpha", ref alpha, 0, 1, "%.2f");
        ImGui.SliderFloat("Dither fade (unknown semantics)", ref ditherFade, 0, 1, "%.2f");
        ImGui.TextUnformatted("Triangle 2: fixed green, alpha 0.75, shifted right.");

        if (ImGui.Button("Reset"))
        {
            offset = default;
            rotationDegrees = 0;
            scale = 1;
            color = new Vector3(1, 0, 0);
            alpha = 0.5f;
            ditherFade = 1;
        }
    }
}
