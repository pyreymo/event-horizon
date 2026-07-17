using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Underpaint;

namespace EventHorizon.Playground3D;

internal sealed class DemoWindow : Window, IDisposable
{
    private const int PlotSamples = 300;

    private readonly UnderpaintRenderer? underpaint;
    private readonly IObjectTable objects;
    private readonly ITargetManager targetManager;
    private readonly ITextureProvider textureProvider;
    private readonly List<DemoObject> demoObjects = [];
    private readonly float[] renderMilliseconds = new float[PlotSamples];
    private readonly float[] renderMillisecondsLinear = new float[PlotSamples];
    private readonly Stopwatch renderStopwatch = new();

    private int nextId = 1;
    private int renderSampleIndex;
    private int targetIndex;
    private float heightOffset = 0.03f;
    private float semitransparentAmbient = SemitransparentLighting.Default.Ambient;
    private float semitransparentDiffuse = SemitransparentLighting.Default.Diffuse;
    private float semitransparentSpecular = SemitransparentLighting.Default.Specular;
    private GBufferTarget? publishedTarget;
    private IDalamudTextureWrap? icon;

    public bool WorldDrawEnabled;
    public bool UsesOpaqueBackend => SelectedTarget == GBufferTarget.Opaque;

    private GBufferTarget SelectedTarget => targetIndex == 0 ? GBufferTarget.Opaque : GBufferTarget.Semitransparent;

    public DemoWindow(UnderpaintRenderer? underpaint, IObjectTable objects, ITargetManager targetManager, ITextureProvider textureProvider)
        : base("Underpaint Demo")
    {
        this.underpaint = underpaint;
        this.objects = objects;
        this.targetManager = targetManager;
        this.textureProvider = textureProvider;
        IsOpen = false;
        SizeCondition = ImGuiCond.FirstUseEver;
        Size = new Vector2(560, 720);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 240),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
        StopWorldDraw();
        icon?.Dispose();
        icon = null;
    }

    public void StopWorldDraw()
    {
        if (publishedTarget is not { } target)
        {
            return;
        }

        underpaint?.Clear(target);
        publishedTarget = null;
    }

    public override void Draw()
    {
        ImGui.Checkbox("Draw enabled", ref WorldDrawEnabled);
        string[] targets = ["Opaque G-buffer", "Semitransparent G-buffer"];
        ImGui.Combo("Target", ref targetIndex, targets, targets.Length);

        if (underpaint is null)
        {
            ImGui.TextDisabled("Underpaint is unavailable.");
        }

        ImGui.SliderFloat("Height offset (m)", ref heightOffset, 0f, 1f, "%.3f");
        if (SelectedTarget == GBufferTarget.Semitransparent)
        {
            ImGui.SliderFloat("Ambient", ref semitransparentAmbient, 0f, 2f, "%.2f");
            ImGui.SliderFloat("Diffuse", ref semitransparentDiffuse, 0f, 8f, "%.2f");
            ImGui.SliderFloat("Specular", ref semitransparentSpecular, 0f, 8f, "%.2f");
        }

        DrawTimingPlot();
        ImGui.Separator();

        var player = objects.LocalPlayer;
        if (player is null)
        {
            ImGui.TextUnformatted("Log into the game to draw.");
            return;
        }

        var position = player.Position;
        var rotation = player.Rotation;
        ImGui.TextUnformatted("Spawn at player position:");
        if (ImGui.Button("Fan"))
            Spawn(new FanObject { Position = position, Rotation = rotation });
        ImGui.SameLine();
        if (ImGui.Button("Triangle"))
            Spawn(new TriangleObject { Position = position, Rotation = rotation });
        ImGui.SameLine();
        if (ImGui.Button("Line to target"))
            Spawn(new LineObject(targetManager) { Position = position });
        if (ImGui.Button("Sphere"))
            Spawn(new SphereObject { Position = position });
        ImGui.SameLine();
        if (ImGui.Button("Image"))
            Spawn(new ImageObject(() => icon) { Position = position });
        ImGui.SameLine();
        if (ImGui.Button("Clear all"))
            demoObjects.Clear();

        ImGui.Separator();
        ImGui.TextUnformatted($"Spawned objects ({demoObjects.Count}):");
        DrawObjectList(position);
    }

    public void DrawWorld()
    {
        renderStopwatch.Restart();
        try
        {
            DrawWorldInner();
        }
        finally
        {
            renderStopwatch.Stop();
            renderMilliseconds[renderSampleIndex] = (float)renderStopwatch.Elapsed.TotalMilliseconds;
            renderSampleIndex = (renderSampleIndex + 1) % PlotSamples;
        }
    }

    private void DrawWorldInner()
    {
        if (underpaint is null || objects.LocalPlayer is null)
        {
            StopWorldDraw();
            return;
        }

        icon ??= textureProvider.GetFromGameIcon(61241).GetWrapOrEmpty();
        var target = SelectedTarget;
        if (publishedTarget is { } previousTarget && previousTarget != target)
        {
            underpaint.Clear(previousTarget);
        }

        using var draw =
            target == GBufferTarget.Semitransparent
                ? underpaint.DrawSemitransparent(
                    lighting: new SemitransparentLighting(semitransparentAmbient, semitransparentDiffuse, semitransparentSpecular)
                )
                : underpaint.DrawOpaque();

        foreach (var demoObject in demoObjects)
        {
            demoObject.Draw(draw, heightOffset);
        }

        publishedTarget = target;
    }

    private void DrawTimingPlot()
    {
        var current = renderMilliseconds[(renderSampleIndex - 1 + PlotSamples) % PlotSamples];
        var peak = 0f;
        for (var index = 0; index < PlotSamples; index++)
        {
            renderMillisecondsLinear[index] = renderMilliseconds[(renderSampleIndex + index) % PlotSamples];
            peak = MathF.Max(peak, renderMillisecondsLinear[index]);
        }

        ImGui.TextUnformatted($"Render: {current:F2} ms (peak {peak:F2} ms)");
        ImGui.PlotLines("##render_time", renderMillisecondsLinear, PlotSamples, "", 0f, float.MaxValue, new Vector2(0, 60));
    }

    private void DrawObjectList(Vector3 playerPosition)
    {
        int? removeIndex = null;
        for (var index = 0; index < demoObjects.Count; index++)
        {
            var demoObject = demoObjects[index];
            ImGui.PushID(demoObject.Id);
            var header =
                $"#{demoObject.Id} {demoObject.TypeName} @ ({demoObject.Position.X:F1}, {demoObject.Position.Y:F1}, {demoObject.Position.Z:F1})###object{demoObject.Id}";
            if (ImGui.CollapsingHeader(header))
            {
                demoObject.DrawUi();
                if (ImGui.Button("Move to player"))
                    demoObject.Position = playerPosition;
                ImGui.SameLine();
                if (ImGui.Button("Remove"))
                    removeIndex = index;
            }
            ImGui.PopID();
        }

        if (removeIndex is { } value)
        {
            demoObjects.RemoveAt(value);
        }
    }

    private void Spawn(DemoObject demoObject)
    {
        demoObject.Id = nextId++;
        demoObjects.Add(demoObject);
    }

    private static uint ToU32(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);

    private abstract class DemoObject
    {
        public int Id;
        public Vector3 Position;
        public abstract string TypeName { get; }
        public abstract void DrawUi();
        public abstract void Draw(GBufferDrawList draw, float heightOffset);
    }

    private sealed class FanObject : DemoObject
    {
        public float Rotation;
        public float InnerRadius;
        public float OuterRadius = 5f;
        public float AngleDegrees = 360f;
        public int Segments;
        public Vector4 InnerColor = new(1f, 0.4f, 0.2f, 0.35f);
        public Vector4 OuterColor = new(1f, 0.4f, 0.2f, 0.35f);
        public override string TypeName => "Fan";

        public override void DrawUi()
        {
            ImGui.SliderFloat("Inner radius", ref InnerRadius, 0f, 20f);
            ImGui.SliderFloat("Outer radius", ref OuterRadius, 0.5f, 30f);
            ImGui.SliderFloat("Angle (deg)", ref AngleDegrees, 1f, 360f);
            ImGui.SliderAngle("Rotation", ref Rotation);
            ImGui.SliderInt("Segments (0 = auto)", ref Segments, 0, 128);
            ImGui.ColorEdit4("Inner color", ref InnerColor);
            ImGui.ColorEdit4("Outer color", ref OuterColor);
        }

        public override void Draw(GBufferDrawList draw, float heightOffset)
        {
            var halfAngle = MathF.PI * AngleDegrees / 360f;
            draw.AddFanFilled(
                Position + (Vector3.UnitY * heightOffset),
                InnerRadius,
                OuterRadius,
                Rotation - halfAngle,
                Rotation + halfAngle,
                ToU32(InnerColor),
                ToU32(OuterColor),
                (uint)Segments
            );
        }
    }

    private sealed class TriangleObject : DemoObject
    {
        public float Rotation;
        public float Reach = 6f;
        public float HalfBase = 3f;
        public Vector4 TipColor = new(0.3f, 0.9f, 1f, 0.6f);
        public Vector4 RightColor = new(0.3f, 0.9f, 1f, 0.6f);
        public Vector4 LeftColor = new(0.3f, 0.9f, 1f, 0.6f);
        public override string TypeName => "Triangle";

        public override void DrawUi()
        {
            ImGui.SliderFloat("Reach", ref Reach, 1f, 30f);
            ImGui.SliderFloat("Half base", ref HalfBase, 0.5f, 20f);
            ImGui.SliderAngle("Rotation", ref Rotation);
            ImGui.ColorEdit4("Tip color", ref TipColor);
            ImGui.ColorEdit4("Right color", ref RightColor);
            ImGui.ColorEdit4("Left color", ref LeftColor);
        }

        public override void Draw(GBufferDrawList draw, float heightOffset)
        {
            var offset = Vector3.UnitY * heightOffset;
            var forward = new Vector3(MathF.Sin(Rotation), 0f, MathF.Cos(Rotation));
            var right = Vector3.Cross(forward, Vector3.UnitY);
            var tip = Position + offset + (forward * Reach);
            var rightCorner = Position + offset + (forward * (Reach * 0.5f)) + (right * HalfBase);
            var leftCorner = Position + offset + (forward * (Reach * 0.5f)) - (right * HalfBase);
            draw.AddTriangleFilled(tip, leftCorner, rightCorner, ToU32(TipColor), ToU32(LeftColor), ToU32(RightColor));
        }
    }

    private sealed class LineObject(ITargetManager targetManager) : DemoObject
    {
        public float HalfWidth = 0.5f;
        public Vector4 Color = new(1f, 0.6f, 0.2f, 1f);
        public override string TypeName => "Line to target";

        public override void DrawUi()
        {
            ImGui.SliderFloat("Half width", ref HalfWidth, 0.05f, 5f);
            ImGui.ColorEdit4("Color", ref Color);
            ImGui.TextUnformatted("Endpoint follows the current target.");
        }

        public override void Draw(GBufferDrawList draw, float heightOffset)
        {
            if (targetManager.Target is not { } target)
                return;

            var offset = Vector3.UnitY * heightOffset;
            var start = Position + offset;
            var stop = target.Position + offset;
            var perpendicular = Vector3.Cross(stop - start, Vector3.UnitY);
            if (perpendicular.LengthSquared() < 1e-8f)
                return;

            perpendicular = HalfWidth * Vector3.Normalize(perpendicular);
            draw.AddQuadFilled(start + perpendicular, stop + perpendicular, stop - perpendicular, start - perpendicular, ToU32(Color));
        }
    }

    private sealed class SphereObject : DemoObject
    {
        public float Radius = 2f;
        public float SphereHeightOffset = 1f;
        public Vector4 Color = new(0.4f, 0.7f, 1f, 0.5f);
        public override string TypeName => "Sphere";

        public override void DrawUi()
        {
            ImGui.SliderFloat("Radius (m)", ref Radius, 0.1f, 30f);
            ImGui.SliderFloat("Height offset (m)", ref SphereHeightOffset, 0f, 10f);
            ImGui.ColorEdit4("Color", ref Color);
        }

        public override void Draw(GBufferDrawList draw, float heightOffset)
        {
            draw.AddSphere(Position + (Vector3.UnitY * (SphereHeightOffset + heightOffset)), Radius, ToU32(Color));
        }
    }

    private sealed class ImageObject(Func<IDalamudTextureWrap?> getIcon) : DemoObject
    {
        public float Width = 4f;
        public float Height = 4f;
        public bool Vertical;
        public float Rotation;
        public override string TypeName => "Image";

        public override void DrawUi()
        {
            ImGui.SliderFloat("Width (m)", ref Width, 0.1f, 30f);
            ImGui.SliderFloat("Height (m)", ref Height, 0.1f, 30f);
            ImGui.Checkbox("Vertical", ref Vertical);
            if (Vertical)
                ImGui.SliderAngle("Rotation", ref Rotation);
        }

        public override void Draw(GBufferDrawList draw, float heightOffset)
        {
            var texture = getIcon();
            if (texture is null)
                return;

            Vector3 right;
            Vector3 down;
            if (Vertical)
            {
                right = new Vector3(Width * MathF.Cos(Rotation), 0f, -Width * MathF.Sin(Rotation));
                down = new Vector3(0f, -Height, 0f);
            }
            else
            {
                right = new Vector3(Width, 0f, 0f);
                down = new Vector3(0f, 0f, Height);
            }

            draw.AddImage(unchecked((nint)texture.Handle.Handle), Position + (Vector3.UnitY * heightOffset), right, down);
        }
    }
}
