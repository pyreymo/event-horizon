using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Underpaint;

namespace EventHorizon.Playground3D;

internal sealed unsafe class DemoWindow : Window
{
    private const ulong TriangleId = 1;
    private const ulong QuadId = 3;
    private const ulong IcosahedronId = 4;
    private const int MainView = 30;
    private const int MainRenderCameraSubView = 12;

    private readonly Renderer? renderer;
    private readonly Primitive[] frame = new Primitive[4];
    private Matrix4x4 anchorWorld;
    private Matrix4x4 currentWorld;
    private Matrix4x4 secondCurrentWorld;
    private Matrix4x4 quadCurrentWorld;
    private Matrix4x4 icosahedronCurrentWorld;
    private bool hasAnchorWorld;
    private Vector3 offset;
    private float groupHorizontalOffset;
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
            quadCurrentWorld = Matrix4x4.CreateTranslation(-1.5f, 0, 0) * anchorWorld;
            icosahedronCurrentWorld = Matrix4x4.CreateTranslation(0, 1.4f, 0) * anchorWorld;
            hasAnchorWorld = true;
        }

        var previousWorld = currentWorld;
        var secondPreviousWorld = secondCurrentWorld;
        var quadPreviousWorld = quadCurrentWorld;
        var icosahedronPreviousWorld = icosahedronCurrentWorld;
        var groupOffset = offset + new Vector3(groupHorizontalOffset, 0, 0);
        currentWorld =
            Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateRotationY(rotationDegrees * MathF.PI / 180)
            * Matrix4x4.CreateTranslation(groupOffset)
            * anchorWorld;
        secondCurrentWorld =
            Matrix4x4.CreateScale(0.75f) * Matrix4x4.CreateTranslation(groupOffset + new Vector3(1.5f, 0, 0)) * anchorWorld;
        quadCurrentWorld = Matrix4x4.CreateScale(0.75f) * Matrix4x4.CreateTranslation(groupOffset + new Vector3(-1.5f, 0, 0)) * anchorWorld;
        icosahedronCurrentWorld =
            Matrix4x4.CreateScale(1.2f)
            * Matrix4x4.CreateFromYawPitchRoll(0.35f, 0.2f, 0)
            * Matrix4x4.CreateTranslation(groupOffset + new Vector3(0, 1.4f, 0))
            * anchorWorld;

        frame[0] = new Primitive(PrimitiveType.Triangle, TriangleId, currentWorld, previousWorld, color, alpha, ditherFade);
        frame[1] = new Primitive(PrimitiveType.Triangle, 2, secondCurrentWorld, secondPreviousWorld, new Vector3(0, 1, 0), 0.75f, 1);
        frame[2] = new Primitive(PrimitiveType.Quad, QuadId, quadCurrentWorld, quadPreviousWorld, new Vector3(0, 0.5f, 1), 0.65f, 1);
        frame[3] = new Primitive(
            PrimitiveType.Icosahedron,
            IcosahedronId,
            icosahedronCurrentWorld,
            icosahedronPreviousWorld,
            new Vector3(1, 0.65f, 0.1f),
            0.75f,
            1
        );
        renderer.SubmitFrame(frame);
    }

    public override void Draw()
    {
        ImGui.DragFloat3("Offset", ref offset, 0.05f);
        ImGui.SliderFloat("Group horizontal offset", ref groupHorizontalOffset, -10, 10, "%.2f");
        ImGui.SliderFloat("Rotation", ref rotationDegrees, -180, 180, "%.0f deg");
        ImGui.SliderFloat("Scale", ref scale, 0.1f, 5, "%.2f");
        ImGui.ColorEdit3("Color", ref color);
        ImGui.SliderFloat("Alpha", ref alpha, 0, 1, "%.2f");
        ImGui.SliderFloat("Dither fade (unknown semantics)", ref ditherFade, 0, 1, "%.2f");
        ImGui.TextUnformatted("Triangle 2: fixed green, alpha 0.75, shifted right.");
        ImGui.TextUnformatted("Quad: fixed blue, alpha 0.65, shifted left.");
        ImGui.TextUnformatted("Icosahedron: fixed orange, alpha 0.75, shifted up.");

        if (ImGui.Button("Reset"))
        {
            offset = default;
            groupHorizontalOffset = 0;
            rotationDegrees = 0;
            scale = 1;
            color = new Vector3(1, 0, 0);
            alpha = 0.5f;
            ditherFade = 1;
        }
    }
}
