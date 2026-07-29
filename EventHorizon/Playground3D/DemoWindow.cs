using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using EventHorizon.Interop.Vfx;
using Underpaint;

namespace EventHorizon.Playground3D;

internal sealed class DemoWindow : Window, IDisposable
{
    private static readonly Matrix4x4 GroundRotation = Matrix4x4.CreateRotationX(-MathF.PI / 2f);

    private readonly Renderer? renderer;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly List<PrimitiveItem> primitives = [];
    private int selectedIndex = -1;
    private int nextPrimitiveNumber = 1;
#if DEBUG
    private Vector3 avfxHostOffset = new(0f, 0f, 2f);
    private Vector3 avfxTransformOffset = new(0f, 0f, 1f);
    private bool animateAvfxColorAndAlpha = true;
    private bool animateAvfxVertices;
    private bool createTwoAvfxInstances = true;
    private Vector3 avfxInstanceSpacing = new(1.5f, 0f, 0f);
#endif

    public DemoWindow(Renderer? renderer, IObjectTable objectTable, IClientState clientState)
        : base("Rendering Research")
    {
        this.renderer = renderer;
        this.objectTable = objectTable;
        this.clientState = clientState;
        clientState.TerritoryChanged += OnTerritoryChanged;
        IsOpen = false;
        SizeCondition = ImGuiCond.FirstUseEver;
        Size = new Vector2(640, 420);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void SubmitFrame()
    {
        if (renderer == null)
            return;

#if DEBUG
        renderer.UpdateAvfxGeometryProbe();
#endif

        using var frame = renderer.BeginFrame();
        if (!IsInGame())
        {
            ClearAll();
            frame.Publish();
            return;
        }

        foreach (var primitive in primitives)
            primitive.Draw(frame);
        frame.Publish();
    }

    public override void Draw()
    {
        if (renderer == null)
        {
            ImGui.TextUnformatted("Underpaint is unavailable.");
            return;
        }

        if (!IsInGame())
        {
            ImGui.TextUnformatted("Primitive drawing is only available in game.");
            return;
        }

        if (ImGui.Button("Add triangle"))
            AddTriangle();
        ImGui.SameLine();
        if (ImGui.Button("Add rectangle"))
            AddRectangle();

        if (selectedIndex >= 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Delete selected"))
                DeleteSelected();
        }

        if (primitives.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear all"))
                ClearAll();
        }

#if DEBUG
        if (primitives.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Capture SortKeys"))
                renderer.ArmSortKeyCapture();
        }

        var sortKeyCaptureStatus = renderer.SortKeyCaptureStatus;
        if (!string.IsNullOrEmpty(sortKeyCaptureStatus))
            ImGui.TextWrapped(sortKeyCaptureStatus);

        ImGui.Separator();
        ImGui.TextUnformatted("AVFX lifecycle shell gate");
        ImGui.DragFloat3("Host offset##AvfxGeometry", ref avfxHostOffset, 0.05f);
        ImGui.DragFloat3("Transform offset##AvfxGeometry", ref avfxTransformOffset, 0.05f);
        ImGui.Checkbox("Animate color and alpha##AvfxGeometry", ref animateAvfxColorAndAlpha);
        ImGui.Checkbox("Animate vertex positions##AvfxGeometry", ref animateAvfxVertices);
        ImGui.Checkbox("Create two instances##AvfxGeometry", ref createTwoAvfxInstances);
        if (createTwoAvfxInstances)
            ImGui.DragFloat3("Instance spacing##AvfxGeometry", ref avfxInstanceSpacing, 0.05f);
        ImGui.TextDisabled("Animation options are applied when the shell starts.");
        if (ImGui.Button("Start lifecycle shell##AvfxGeometry"))
            StartAvfxGeometryProbe();
        ImGui.SameLine();
        if (ImGui.Button("Stop##AvfxGeometry"))
            renderer.StopAvfxGeometryProbe();
        ImGui.TextWrapped(renderer.AvfxGeometryProbeStatus);
#endif

        ImGui.Separator();
        if (!ImGui.BeginTable("Primitive editor", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("Primitives", ImGuiTableColumnFlags.WidthFixed, 180f);
        ImGui.TableSetupColumn("Properties", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        for (var index = 0; index < primitives.Count; index++)
        {
            if (ImGui.Selectable(primitives[index].Name, selectedIndex == index))
                selectedIndex = index;
        }

        ImGui.TableNextColumn();
        if (selectedIndex >= 0 && selectedIndex < primitives.Count)
            DrawEditor(primitives[selectedIndex]);
        else
            ImGui.TextUnformatted("Select a primitive to edit it.");

        ImGui.EndTable();
    }

    public void Dispose()
    {
        clientState.TerritoryChanged -= OnTerritoryChanged;
#if DEBUG
        renderer?.StopAvfxGeometryProbe();
#endif
        ClearAll();
    }

    private bool IsInGame()
    {
        var localPlayer = objectTable.LocalPlayer;
        return clientState.IsLoggedIn && localPlayer != null && !localPlayer.IsDead;
    }

    private void AddTriangle()
    {
        var localPlayer = objectTable.LocalPlayer;
        if (renderer == null || localPlayer == null)
            return;

        primitives.Add(
            new PrimitiveItem(
                $"Triangle {nextPrimitiveNumber++}",
                renderer.CreateTriangle(),
                localPlayer.Position,
                new Vector4(1f, 0.15f, 0.1f, 0.75f)
            )
        );
        selectedIndex = primitives.Count - 1;
    }

    private void AddRectangle()
    {
        var localPlayer = objectTable.LocalPlayer;
        if (renderer == null || localPlayer == null)
            return;

        primitives.Add(
            new PrimitiveItem(
                $"Rectangle {nextPrimitiveNumber++}",
                renderer.CreateRectangle(2f, 1f),
                localPlayer.Position,
                new Vector4(0.1f, 0.5f, 1f, 0.65f)
            )
        );
        selectedIndex = primitives.Count - 1;
    }

    private static void DrawEditor(PrimitiveItem primitive)
    {
        ImGui.TextUnformatted(primitive.Name);
        ImGui.Separator();
        ImGui.DragFloat3("Position", ref primitive.Position, 0.05f);
        ImGui.ColorEdit4("Color", ref primitive.Color, ImGuiColorEditFlags.DisplayRgb | ImGuiColorEditFlags.InputRgb);
        ImGui.SliderFloat("Dither", ref primitive.Dither, 0f, 1f, "%.2f");

        if (primitive.Drawable is not RectangleDrawable rectangle)
            return;

        var width = rectangle.Width;
        var height = rectangle.Height;
        var sizeChanged = ImGui.SliderFloat("Width", ref width, 0.1f, 10f, "%.2f");
        sizeChanged |= ImGui.SliderFloat("Height", ref height, 0.1f, 10f, "%.2f");
        if (sizeChanged)
            rectangle.Resize(width, height);
    }

    private void DeleteSelected()
    {
        primitives[selectedIndex].Dispose();
        primitives.RemoveAt(selectedIndex);
        selectedIndex = Math.Min(selectedIndex, primitives.Count - 1);
    }

    private void ClearAll()
    {
        foreach (var primitive in primitives)
            primitive.Dispose();
        primitives.Clear();
        selectedIndex = -1;
    }

    private void OnTerritoryChanged(uint _)
    {
#if DEBUG
        renderer?.StopAvfxGeometryProbe();
#endif
        ClearAll();
    }

#if DEBUG
    private void StartAvfxGeometryProbe()
    {
        var localPlayer = objectTable.LocalPlayer;
        if (renderer == null || localPlayer == null)
            return;

        renderer.StartAvfxGeometryProbe(
            StaticVfxResourceRedirector.UnderpaintShellPath,
            localPlayer.Position + avfxHostOffset,
            avfxTransformOffset,
            animateAvfxColorAndAlpha,
            animateAvfxVertices,
            createTwoAvfxInstances ? 2 : 1,
            avfxInstanceSpacing
        );
    }
#endif

    private sealed class PrimitiveItem(string name, IDisposable drawable, Vector3 position, Vector4 color) : IDisposable
    {
        internal string Name { get; } = name;
        internal IDisposable Drawable { get; } = drawable;
        internal Vector3 Position = position;
        internal Vector4 Color = color;
        internal float Dither = 1f;

        internal void Draw(PrimitiveFrame frame)
        {
            var transform = GroundRotation * Matrix4x4.CreateTranslation(Position);
            var color = new Vector3(Color.X, Color.Y, Color.Z);
            switch (Drawable)
            {
                case TriangleDrawable triangle:
                    frame.DrawTriangle(triangle, transform, color, Color.W, Dither);
                    break;
                case RectangleDrawable rectangle:
                    frame.DrawRectangle(rectangle, transform, color, Color.W, Dither);
                    break;
            }
        }

        public void Dispose() => Drawable.Dispose();
    }
}
