using System.Diagnostics;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using EventHorizon.Integration.Debug;
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
    private float heightOffset = 0.03f;
    private float semitransparentAmbient = SemitransparentLighting.Default.Ambient;
    private float semitransparentDiffuse = SemitransparentLighting.Default.Diffuse;
    private float semitransparentSpecular = SemitransparentLighting.Default.Specular;
    private GBufferMaterial opaqueMaterial = GBufferMaterial.Default;
    private GBufferMaterial semitransparentMaterial = GBufferMaterial.Default;
    private bool opaquePublished;
    private bool semitransparentPublished;
    private Vector2 diagnosticJitterPixels;
    private int diagnosticOpaqueDepthBias;
    private bool diagnosticForceOpaqueAlpha = true;
    private NativeDrawSnapshot? diagnosticSnapshot;
#if DEBUG
    private NativeOpaquePreviewController? nativeOpaquePreview;
    private string clearDebugLogState = "";
#endif
    private bool boundaryStabilityTestEnabled;
    private Vector3 boundaryStabilityTestPosition;
    private float boundaryStabilityTestRotation;
    private IDalamudTextureWrap? icon;

    public bool WorldDrawEnabled;
    public bool UsesOpaqueBackend =>
        opaquePublished || boundaryStabilityTestEnabled || demoObjects.Any(demoObject => demoObject.DrawsTo(GBufferTarget.Opaque));

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

#if DEBUG
    public void AttachNativeOpaquePreview(NativeOpaquePreviewController preview) => nativeOpaquePreview = preview;
#endif

    public void StopWorldDraw()
    {
        if (underpaint is null)
        {
            return;
        }

        underpaint.Diagnostics.OpaqueJitterPixels = Vector2.Zero;
        underpaint.Diagnostics.OpaqueDepthBias = 0;
        underpaint.Diagnostics.ForceOpaqueAlpha = false;

        if (opaquePublished)
        {
            underpaint.Clear(GBufferTarget.Opaque);
            opaquePublished = false;
        }

        if (semitransparentPublished)
        {
            underpaint.Clear(GBufferTarget.Semitransparent);
            semitransparentPublished = false;
        }
    }

    public override void Draw()
    {
        ImGui.Checkbox("Draw enabled", ref WorldDrawEnabled);
        if (underpaint is null)
        {
            ImGui.TextDisabled("Underpaint is unavailable.");
        }
        else
        {
            DrawDiagnostics();
#if DEBUG
            DrawNativeOpaquePreviewUi();
#endif
        }

        ImGui.SliderFloat("Height offset (m)", ref heightOffset, -10f, 10f, "%.3f");
        DrawBoundaryStabilityTestUi();
        if (ImGui.CollapsingHeader("Semitransparent lighting"))
        {
            ImGui.SliderFloat("Ambient", ref semitransparentAmbient, 0f, 2f, "%.2f");
            ImGui.SliderFloat("Diffuse", ref semitransparentDiffuse, 0f, 8f, "%.2f");
            ImGui.SliderFloat("Specular", ref semitransparentSpecular, 0f, 8f, "%.2f");
        }

        if (ImGui.CollapsingHeader("Opaque material"))
            DrawMaterialEditor("opaque", ref opaqueMaterial);
        if (ImGui.CollapsingHeader("Semitransparent material"))
            DrawMaterialEditor("semitransparent", ref semitransparentMaterial);

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
        if (ImGui.Button("Quad"))
            Spawn(new QuadObject { Position = position, Rotation = rotation });
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

#if DEBUG
    private void DrawNativeOpaquePreviewUi()
    {
        if (nativeOpaquePreview == null || !ImGui.CollapsingHeader("Native opaque preview"))
            return;

        ImGui.TextDisabled(nativeOpaquePreview.State);
        if (!nativeOpaquePreview.IsVisible)
        {
            if (ImGui.Button("Show native opaque preview"))
                nativeOpaquePreview.Show();
        }
        else if (ImGui.Button("Hide native opaque preview"))
            nativeOpaquePreview.Hide();
        ImGui.SameLine();
        if (ImGui.Button("Clear EventHorizon logs"))
            clearDebugLogState = DebugFileLog.Clear();
        if (clearDebugLogState.Length != 0)
            ImGui.TextDisabled(clearDebugLogState);
        ImGui.TextDisabled("Show places a small native panel at a fixed world position three metres ahead.");
    }
#endif

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
        underpaint.Diagnostics.OpaqueJitterPixels = boundaryStabilityTestEnabled ? Vector2.Zero : diagnosticJitterPixels;
        underpaint.Diagnostics.OpaqueDepthBias = boundaryStabilityTestEnabled ? 0 : diagnosticOpaqueDepthBias;
        underpaint.Diagnostics.ForceOpaqueAlpha = boundaryStabilityTestEnabled || diagnosticForceOpaqueAlpha;
        PublishOpaque();
        PublishSemitransparent();
    }

    private void DrawBoundaryStabilityTestUi()
    {
        var wasEnabled = boundaryStabilityTestEnabled;
        if (ImGui.Checkbox("Opaque boundary A/B test", ref boundaryStabilityTestEnabled))
        {
            if (!wasEnabled && boundaryStabilityTestEnabled && objects.LocalPlayer is { } player)
            {
                boundaryStabilityTestPosition = player.Position;
                boundaryStabilityTestRotation = player.Rotation;
            }
        }

        if (!boundaryStabilityTestEnabled)
            return;

        ImGui.TextDisabled("A: vertical quad intersecting native ground (depth edge)");
        ImGui.TextDisabled("B: elevated coplanar quads (hard color edge)");
        if (ImGui.Button("Move A/B test to player") && objects.LocalPlayer is { } currentPlayer)
        {
            boundaryStabilityTestPosition = currentPlayer.Position;
            boundaryStabilityTestRotation = currentPlayer.Rotation;
        }
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

    private void DrawDiagnostics()
    {
        if (underpaint == null)
            return;

        if (underpaint.Diagnostics.TryTakeOpaqueDrawSnapshot(out var snapshot))
            diagnosticSnapshot = snapshot;

        if (!ImGui.CollapsingHeader("Projection diagnostics"))
            return;

        ImGui.Checkbox("Force opaque alpha = 1", ref diagnosticForceOpaqueAlpha);
        ImGui.SliderFloat2("Jitter pixels", ref diagnosticJitterPixels, -1f, 1f, "%.3f");
        ImGui.SliderInt("Opaque depth bias", ref diagnosticOpaqueDepthBias, 0, 64);
        underpaint.Diagnostics.ForceOpaqueAlpha = diagnosticForceOpaqueAlpha;
        underpaint.Diagnostics.OpaqueJitterPixels = diagnosticJitterPixels;
        underpaint.Diagnostics.OpaqueDepthBias = diagnosticOpaqueDepthBias;

        if (ImGui.Button("Capture next native opaque draw"))
            underpaint.Diagnostics.RequestOpaqueDrawSnapshot();
        ImGui.SameLine();
        ImGui.TextDisabled("One-shot GPU readback");

        if (diagnosticSnapshot is not { } currentSnapshot)
            return;

        ImGui.TextUnformatted($"Snapshot #{currentSnapshot.Sequence} at {currentSnapshot.CapturedAt:O}");
        if (ImGui.Button("Copy snapshot"))
            ImGui.SetClipboardText(BuildSnapshotText(currentSnapshot));

        if (!ImGui.TreeNode("Snapshot details"))
            return;

        foreach (var viewport in currentSnapshot.Viewports)
        {
            ImGui.TextUnformatted(
                $"Viewport: ({viewport.X}, {viewport.Y}) {viewport.Width}x{viewport.Height}, depth={viewport.MinDepth}..{viewport.MaxDepth}"
            );
        }
        if (currentSnapshot.Rasterizer is { } rasterizer)
        {
            ImGui.TextUnformatted(
                $"Rasterizer: fill={rasterizer.FillMode}, cull={rasterizer.CullMode}, frontCCW={rasterizer.FrontCounterClockwise}, scissor={rasterizer.Scissor}"
            );
        }
        if (currentSnapshot.DepthStencil is { } depthStencil)
        {
            ImGui.TextUnformatted(
                $"Depth: enabled={depthStencil.DepthEnabled}, write={depthStencil.DepthWriteMask}, compare={depthStencil.DepthComparison}, stencil={depthStencil.StencilEnabled}/0x{depthStencil.StencilReference:X}"
            );
        }
        for (var index = 0; index < currentSnapshot.RenderTargets.Count; index++)
            ImGui.TextUnformatted($"RT{index}: {currentSnapshot.RenderTargets[index]}");
        ImGui.TextUnformatted($"DSV: {currentSnapshot.DepthTarget}");
        ImGui.TextUnformatted($"VS: 0x{currentSnapshot.VertexShader:X}");
        foreach (var buffer in currentSnapshot.VertexConstantBuffers)
        {
            ImGui.TextUnformatted($"VS CB b{buffer.Slot}: 0x{buffer.Pointer:X}, {buffer.ByteWidth} bytes, hash={buffer.ContentHash:X16}");
        }
        DrawMatrix("Control VP", currentSnapshot.ControlViewProjection);
        if (currentSnapshot.SceneViewProjection is { } sceneViewProjection)
            DrawMatrix("Scene view * render projection", sceneViewProjection);
        if (currentSnapshot.CameraParameter is { } cameraParameter)
        {
            ImGui.TextUnformatted(
                $"CameraParameter found at VS b{cameraParameter.Slot}+0x{cameraParameter.ByteOffset:X}, error={cameraParameter.MatchError:G6}, transposed={cameraParameter.Transposed}"
            );
            DrawMatrix("Native CameraParameter VP", cameraParameter.ViewProjection);
            DrawMatrix("Native CameraParameter projection", cameraParameter.Projection);
            DrawMatrix("Native CameraParameter main-view-to-projection", cameraParameter.MainViewToProjection);
        }
        else
        {
            ImGui.TextDisabled("No CameraParameter-shaped block found in VS b0-b13.");
        }
        ImGui.TreePop();
    }

    private static void DrawMatrix(string label, Matrix4x4 matrix)
    {
        ImGui.TextUnformatted(label);
        ImGui.TextUnformatted($"[{matrix.M11:G7}, {matrix.M12:G7}, {matrix.M13:G7}, {matrix.M14:G7}]");
        ImGui.TextUnformatted($"[{matrix.M21:G7}, {matrix.M22:G7}, {matrix.M23:G7}, {matrix.M24:G7}]");
        ImGui.TextUnformatted($"[{matrix.M31:G7}, {matrix.M32:G7}, {matrix.M33:G7}, {matrix.M34:G7}]");
        ImGui.TextUnformatted($"[{matrix.M41:G7}, {matrix.M42:G7}, {matrix.M43:G7}, {matrix.M44:G7}]");
    }

    private static string BuildSnapshotText(NativeDrawSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Underpaint native opaque snapshot #{snapshot.Sequence} ({snapshot.CapturedAt:O})");
        foreach (var viewport in snapshot.Viewports)
            builder.AppendLine(
                $"Viewport=({viewport.X},{viewport.Y}) {viewport.Width}x{viewport.Height} depth={viewport.MinDepth}..{viewport.MaxDepth}"
            );
        foreach (var scissor in snapshot.Scissors)
            builder.AppendLine($"Scissor=({scissor.Left},{scissor.Top})..({scissor.Right},{scissor.Bottom})");
        builder.AppendLine($"Rasterizer={snapshot.Rasterizer}");
        builder.AppendLine($"DepthStencil={snapshot.DepthStencil}");
        builder.AppendLine($"VS=0x{snapshot.VertexShader:X}");
        for (var index = 0; index < snapshot.RenderTargets.Count; index++)
            builder.AppendLine($"RT{index}={snapshot.RenderTargets[index]}");
        builder.AppendLine($"DSV={snapshot.DepthTarget}");
        foreach (var buffer in snapshot.VertexConstantBuffers)
            builder.AppendLine($"VS.CB{buffer.Slot}=0x{buffer.Pointer:X} bytes={buffer.ByteWidth} hash={buffer.ContentHash:X16}");
        AppendMatrix(builder, "ControlVP", snapshot.ControlViewProjection);
        if (snapshot.SceneViewProjection is { } sceneViewProjection)
            AppendMatrix(builder, "SceneViewProjection", sceneViewProjection);
        if (snapshot.CameraParameter is { } cameraParameter)
        {
            builder.AppendLine(
                $"CameraParameter=VS.CB{cameraParameter.Slot}+0x{cameraParameter.ByteOffset:X} error={cameraParameter.MatchError:R} transposed={cameraParameter.Transposed}"
            );
            AppendMatrix(builder, "NativeVP", cameraParameter.ViewProjection);
            AppendMatrix(builder, "NativeProjection", cameraParameter.Projection);
            AppendMatrix(builder, "NativeMainViewToProjection", cameraParameter.MainViewToProjection);
        }
        return builder.ToString();
    }

    private static void AppendMatrix(StringBuilder builder, string name, Matrix4x4 matrix)
    {
        builder.AppendLine(
            $"{name}=[{matrix.M11:R},{matrix.M12:R},{matrix.M13:R},{matrix.M14:R};{matrix.M21:R},{matrix.M22:R},{matrix.M23:R},{matrix.M24:R};{matrix.M31:R},{matrix.M32:R},{matrix.M33:R},{matrix.M34:R};{matrix.M41:R},{matrix.M42:R},{matrix.M43:R},{matrix.M44:R}]"
        );
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
                demoObject.DrawTargetUi();
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

    private void PublishOpaque()
    {
        var hasObjects = boundaryStabilityTestEnabled || demoObjects.Any(demoObject => demoObject.DrawsTo(GBufferTarget.Opaque));
        if (!hasObjects)
        {
            if (opaquePublished)
            {
                underpaint!.Clear(GBufferTarget.Opaque);
                opaquePublished = false;
            }
            return;
        }

        using var draw = underpaint!.DrawOpaque(opaqueMaterial);
        foreach (var demoObject in demoObjects)
        {
            if (demoObject.DrawsTo(GBufferTarget.Opaque))
                demoObject.Draw(draw, heightOffset);
        }
        if (boundaryStabilityTestEnabled)
            DrawBoundaryStabilityTest(draw);
        opaquePublished = true;
    }

    private void DrawBoundaryStabilityTest(GBufferDrawList draw)
    {
        const float distance = 5f;
        const float sideOffset = 2.5f;
        const float halfSize = 1.5f;
        const float elevatedHeight = 1f;

        var up = Vector3.UnitY;
        var forward = new Vector3(MathF.Sin(boundaryStabilityTestRotation), 0f, MathF.Cos(boundaryStabilityTestRotation));
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        var origin = boundaryStabilityTestPosition + forward * distance;

        // A: one vertical quad crossing the player's ground height. Its lower boundary is owned
        // by the native depth buffer rather than by custom color or clip logic.
        var depthCenter = origin - right * sideOffset;
        draw.AddQuadFilled(
            depthCenter - right * halfSize - up * halfSize,
            depthCenter + right * halfSize - up * halfSize,
            depthCenter + right * halfSize + up * halfSize,
            depthCenter - right * halfSize + up * halfSize,
            0xFF40D0FF
        );

        // B: two adjacent horizontal quads at exactly the same elevated world height. Their
        // shared edge is a pure color discontinuity and never competes with native scene depth.
        var colorCenter = origin + right * sideOffset + up * elevatedHeight;
        draw.AddQuadFilled(
            colorCenter - right * halfSize - forward * halfSize,
            colorCenter - forward * halfSize,
            colorCenter + forward * halfSize,
            colorCenter - right * halfSize + forward * halfSize,
            0xFFFF5050
        );
        draw.AddQuadFilled(
            colorCenter - forward * halfSize,
            colorCenter + right * halfSize - forward * halfSize,
            colorCenter + right * halfSize + forward * halfSize,
            colorCenter + forward * halfSize,
            0xFF50A0FF
        );
    }

    private void PublishSemitransparent()
    {
        var hasObjects = demoObjects.Any(demoObject => demoObject.DrawsTo(GBufferTarget.Semitransparent));
        if (!hasObjects)
        {
            if (semitransparentPublished)
            {
                underpaint!.Clear(GBufferTarget.Semitransparent);
                semitransparentPublished = false;
            }
            return;
        }

        using var draw = underpaint!.DrawSemitransparent(
            semitransparentMaterial,
            new SemitransparentLighting(semitransparentAmbient, semitransparentDiffuse, semitransparentSpecular)
        );
        foreach (var demoObject in demoObjects)
        {
            if (demoObject.DrawsTo(GBufferTarget.Semitransparent))
                demoObject.Draw(draw, heightOffset);
        }
        semitransparentPublished = true;
    }

    private static void DrawMaterialEditor(string id, ref GBufferMaterial material)
    {
        ImGui.PushID(id);
        var g0 = material.G0;
        var g1 = material.G1;
        var g2 = material.G2;
        var g3 = material.G3;
        var g4 = material.G4;
        var stencil = (int)material.Stencil;
        var changed = ImGui.InputFloat4("G0", ref g0);
        changed |= ImGui.InputFloat4("G1", ref g1);
        changed |= ImGui.InputFloat4("G2", ref g2);
        changed |= ImGui.InputFloat4("G3", ref g3);
        changed |= ImGui.InputFloat4("G4", ref g4);
        if (ImGui.InputInt("Stencil", ref stencil))
        {
            stencil = Math.Clamp(stencil, byte.MinValue, byte.MaxValue);
            changed = true;
        }

        if (changed)
            material = new GBufferMaterial(g0, g1, g2, g3, g4, (byte)stencil);
        if (ImGui.Button("Reset"))
            material = GBufferMaterial.Default;
        ImGui.PopID();
    }

    private static uint ToU32(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);

    private abstract class DemoObject
    {
        public int Id;
        public Vector3 Position;
        private int targetIndex;
        public abstract string TypeName { get; }

        public bool DrawsTo(GBufferTarget target) =>
            targetIndex == 2
            || (targetIndex == 0 && target == GBufferTarget.Opaque)
            || (targetIndex == 1 && target == GBufferTarget.Semitransparent);

        public void DrawTargetUi()
        {
            string[] targets = ["Opaque", "Semitransparent", "Both"];
            ImGui.Combo("Target", ref targetIndex, targets, targets.Length);
        }

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

    private sealed class QuadObject : DemoObject
    {
        public float Rotation;
        public float Width = 4f;
        public float Depth = 4f;
        public Vector4 Color = new(0.5f, 1f, 0.4f, 0.6f);
        public override string TypeName => "Quad";

        public override void DrawUi()
        {
            ImGui.SliderFloat("Width", ref Width, 0.1f, 30f);
            ImGui.SliderFloat("Depth", ref Depth, 0.1f, 30f);
            ImGui.SliderAngle("Rotation", ref Rotation);
            ImGui.ColorEdit4("Color", ref Color);
        }

        public override void Draw(GBufferDrawList draw, float heightOffset)
        {
            var center = Position + Vector3.UnitY * heightOffset;
            var forward = new Vector3(MathF.Sin(Rotation), 0f, MathF.Cos(Rotation)) * (Depth * 0.5f);
            var right = Vector3.Cross(forward, Vector3.UnitY);
            if (right.LengthSquared() < 1e-8f)
                return;

            right = Vector3.Normalize(right) * (Width * 0.5f);
            draw.AddQuadFilled(
                center - forward - right,
                center - forward + right,
                center + forward + right,
                center + forward - right,
                ToU32(Color)
            );
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
        public int LatitudeSegments = 8;
        public int LongitudeSegments = 16;
        public Vector4 Color = new(0.4f, 0.7f, 1f, 0.5f);
        public override string TypeName => "Sphere";

        public override void DrawUi()
        {
            ImGui.SliderFloat("Radius (m)", ref Radius, 0.1f, 30f);
            ImGui.SliderFloat("Height offset (m)", ref SphereHeightOffset, -10f, 10f);
            ImGui.SliderInt("Latitude segments", ref LatitudeSegments, 3, (int)GBufferDrawList.MaxSphereLatitudeSegments);
            ImGui.SliderInt("Longitude segments", ref LongitudeSegments, 3, (int)GBufferDrawList.MaxSphereLongitudeSegments);
            ImGui.ColorEdit4("Color", ref Color);
        }

        public override void Draw(GBufferDrawList draw, float heightOffset)
        {
            draw.AddSphere(
                Position + (Vector3.UnitY * (SphereHeightOffset + heightOffset)),
                Radius,
                ToU32(Color),
                (uint)LatitudeSegments,
                (uint)LongitudeSegments
            );
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
