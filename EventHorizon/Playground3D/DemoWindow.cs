using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
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

    public DemoWindow(Renderer? renderer, IObjectTable objectTable, IClientState clientState)
        : base("Underpaint Primitives")
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
        ImGui.SameLine();
        if (ImGui.Button("Add sphere"))
            AddSphere();
        ImGui.SameLine();
        if (ImGui.Button("Add animated decal ring"))
            AddAnimatedDecalRing();
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
        ClearAll();
    }

    private bool IsInGame()
    {
        var localPlayer = objectTable.LocalPlayer;
        return clientState.IsLoggedIn && localPlayer != null && !localPlayer.IsDead;
    }

    private void AddTriangle() => Add("Triangle", renderer!.CreateTriangle(), new Vector4(1f, 0.15f, 0.1f, 0.75f));

    private void AddRectangle() =>
        Add("Rectangle", renderer!.CreateRectangle(2f, 1f), new Vector4(0.1f, 0.5f, 1f, 0.65f));

    private void AddSphere() => Add("Sphere", renderer!.CreateSphere(1f), new Vector4(0.5f, 0.2f, 1f, 0.65f));

    private void AddAnimatedDecalRing() =>
        Add("Animated decal ring", renderer!.CreateAnimatedDecalRing(), new Vector4(1f, 0.25f, 0.05f, 1f));

    private void Add(string kind, IDisposable drawable, Vector4 color)
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            drawable.Dispose();
            return;
        }
        primitives.Add(new PrimitiveItem($"{kind} {nextPrimitiveNumber++}", drawable, localPlayer.Position, color));
        selectedIndex = primitives.Count - 1;
    }

    private static void DrawEditor(PrimitiveItem primitive)
    {
        ImGui.TextUnformatted(primitive.Name);
        ImGui.Separator();
        ImGui.DragFloat3("Position", ref primitive.Position, 0.05f);
        ImGui.ColorEdit4("Color", ref primitive.Color, ImGuiColorEditFlags.DisplayRgb | ImGuiColorEditFlags.InputRgb);

        if (primitive.Drawable is RectangleDrawable rectangle)
        {
            var width = rectangle.Width;
            var height = rectangle.Height;
            var changed = ImGui.SliderFloat("Width", ref width, 0.1f, 10f, "%.2f");
            changed |= ImGui.SliderFloat("Height", ref height, 0.1f, 10f, "%.2f");
            if (changed)
                rectangle.Resize(width, height);
        }
        else if (primitive.Drawable is SphereDrawable sphere)
        {
            var radius = sphere.Radius;
            if (ImGui.SliderFloat("Radius", ref radius, 0.1f, 10f, "%.2f"))
                sphere.Resize(radius);
        }
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

    private void OnTerritoryChanged(uint _) => ClearAll();

    private sealed class PrimitiveItem(string name, IDisposable drawable, Vector3 position, Vector4 color) : IDisposable
    {
        internal string Name { get; } = name;
        internal IDisposable Drawable { get; } = drawable;
        internal Vector3 Position = position;
        internal Vector4 Color = color;

        internal void Draw(PrimitiveFrame frame)
        {
            var transform = Drawable switch
            {
                SphereDrawable => Matrix4x4.CreateTranslation(Position),
                DecalRingDrawable => Matrix4x4.CreateScale(3f) * Matrix4x4.CreateTranslation(Position),
                _ => GroundRotation * Matrix4x4.CreateTranslation(Position),
            };
            var color = new Vector3(Color.X, Color.Y, Color.Z);
            switch (Drawable)
            {
                case TriangleDrawable triangle:
                    frame.DrawTriangle(triangle, transform, Position, color, Color.W);
                    break;
                case RectangleDrawable rectangle:
                    frame.DrawRectangle(rectangle, transform, Position, color, Color.W);
                    break;
                case SphereDrawable sphere:
                    frame.DrawSphere(sphere, transform, Position, color, Color.W);
                    break;
                case DecalRingDrawable ring:
                    frame.DrawAnimatedDecalRing(ring, transform, color, Color.W);
                    break;
            }
        }

        public void Dispose() => Drawable.Dispose();
    }
}
