using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Underpaint;

namespace EventHorizon.Playground3D;

internal sealed class DemoWindow : Window, IDisposable
{
    private static readonly Matrix4x4 GroundRotation = Matrix4x4.CreateRotationX(-MathF.PI / 2f);
    private static readonly Vector3 TriangleColor = new(1f, 0.15f, 0.1f);
    private static readonly Vector3 RectangleColor = new(0.1f, 0.5f, 1f);

    private readonly Renderer? renderer;
    private readonly IObjectTable objectTable;
    private TriangleDrawable? triangle;
    private RectangleDrawable? rectangle;
    private Vector3 trianglePosition;
    private Vector3 rectanglePosition;
    private float rectangleWidth = 2f;
    private float rectangleHeight = 1f;

    public DemoWindow(Renderer? renderer, IObjectTable objectTable)
        : base("Rendering Research")
    {
        this.renderer = renderer;
        this.objectTable = objectTable;
        IsOpen = false;
        SizeCondition = ImGuiCond.FirstUseEver;
        Size = new Vector2(420, 260);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 180),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void SubmitFrame()
    {
        if (renderer == null)
            return;

        using var frame = renderer.BeginFrame();
        if (triangle != null)
            frame.DrawTriangle(triangle, GroundRotation * Matrix4x4.CreateTranslation(trianglePosition), TriangleColor, 0.75f);
        if (rectangle != null)
            frame.DrawRectangle(rectangle, GroundRotation * Matrix4x4.CreateTranslation(rectanglePosition), RectangleColor, 0.65f);
        frame.Publish();
    }

    public override void Draw()
    {
        if (renderer == null)
        {
            ImGui.TextUnformatted("Underpaint is unavailable.");
            return;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (triangle == null)
        {
            if (ImGui.Button("Generate triangle") && localPlayer != null)
            {
                triangle = renderer.CreateTriangle();
                trianglePosition = localPlayer.Position;
            }
        }
        else
        {
            ImGui.DragFloat3("Triangle position", ref trianglePosition, 0.05f);
        }

        if (rectangle == null)
        {
            if (ImGui.Button("Generate rectangle") && localPlayer != null)
            {
                rectangle = renderer.CreateRectangle(rectangleWidth, rectangleHeight);
                rectanglePosition = localPlayer.Position;
            }
        }
        else
        {
            ImGui.DragFloat3("Rectangle position", ref rectanglePosition, 0.05f);
            var sizeChanged = ImGui.SliderFloat("Rectangle width", ref rectangleWidth, 0.1f, 10f, "%.2f");
            sizeChanged |= ImGui.SliderFloat("Rectangle height", ref rectangleHeight, 0.1f, 10f, "%.2f");
            if (sizeChanged)
                rectangle.Resize(rectangleWidth, rectangleHeight);
        }

        if (localPlayer == null && (triangle == null || rectangle == null))
            ImGui.TextUnformatted("Local player is unavailable.");
    }

    public void Dispose()
    {
        rectangle?.Dispose();
        triangle?.Dispose();
    }
}
