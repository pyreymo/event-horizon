#if DEBUG
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using Underpaint;
using Underpaint.Internal;

namespace EventHorizon.Integration.Debug;

internal sealed unsafe class NativeOpaquePreviewController : IDisposable
{
    private const string LogSource = "NativeOpaquePreview";
    private readonly UnderpaintRenderer? underpaint;
    private NativeGeometry? geometry;
    private NativeRigidInstance[] instances = [];
    private Matrix4x4 previewWorld;
    private Vector3 centerColor = Vector3.UnitY;
    private bool drawCaptureActive;
    private string state = "Hidden";

    public NativeOpaquePreviewController(UnderpaintRenderer? underpaint)
    {
        this.underpaint = underpaint;
    }

    public bool IsVisible => instances.Length != 0;
    public string State => state;
    public Vector3 CenterColor => centerColor;

    public void SetCenterColor(Vector3 value)
    {
        centerColor = Vector3.Clamp(value, Vector3.Zero, Vector3.One);
        if (instances.Length >= 2)
            instances[1].UpdateColor(new Vector4(centerColor, 1f));
    }

    public void Show()
    {
        if (instances.Length != 0)
            return;
        if (underpaint == null)
        {
            state = "Unavailable: Underpaint failed to initialize";
            return;
        }
        if (!TryCreatePreviewWorld(out previewWorld))
        {
            state = "Unavailable: no active world camera";
            return;
        }
        geometry ??= underpaint.CreateNativeGeometry(
            [new(-0.8f, -0.8f, 0f), new(0.8f, -0.8f, 0f), new(0.8f, 0.8f, 0f), new(-0.8f, 0.8f, 0f)],
            [new(0f, 1f), new(1f, 1f), new(1f, 0f), new(0f, 0f)],
            [0, 1, 2, 2, 3, 0]
        );
        var created = new List<NativeRigidInstance>(3);
        try
        {
            created.Add(underpaint.CreateNativeWorldRigidInstance(geometry, OffsetWorld(previewWorld, -0.9f), new Vector4(1f, 0f, 0f, 1f)));
            created.Add(underpaint.CreateNativeWorldRigidInstance(geometry, previewWorld, new Vector4(centerColor, 1f)));
            created.Add(underpaint.CreateNativeWorldRigidInstance(geometry, OffsetWorld(previewWorld, 0.9f), new Vector4(0f, 0f, 1f, 1f)));
            instances = created.ToArray();
        }
        catch
        {
            foreach (var item in created)
                item.Dispose();
            throw;
        }
        instances[0].BeginSubmissionCapture(8);
        underpaint.BeginNativeGeometryDrawCapture(geometry);
        drawCaptureActive = true;
        state = "Waiting for the native solid-color rendezvous";
        DebugFileLog.Information(LogSource, "Native RGB solid preview shown; geometry/material/world/color/selection are owned");
    }

    public void Hide()
    {
        var current = instances;
        CompleteDrawCapture("preview-hidden");
        instances = [];
        previewWorld = default;
        var submissionCount = current.Sum(item => item.SubmissionCount);
        foreach (var item in current)
            item.Dispose();
        state = "Hidden";
        if (current.Length != 0)
            DebugFileLog.Information(
                LogSource,
                "Native opaque preview hidden; BuilderSubmissions={BuilderSubmissions} IndexCount=6",
                submissionCount
            );
    }

    public void Update()
    {
        var current = instances;
        if (current.Length == 0)
            return;
        if (current.FirstOrDefault(item => item.Failure != null)?.Failure is { } failure)
        {
            Hide();
            state = $"Stopped: {failure}";
            return;
        }
        try
        {
            if (drawCaptureActive && current[0].SubmissionCount >= 4)
                CompleteDrawCapture("four-builder-submissions");
            if (current.All(item => item.HasSubmitted))
                state = $"Submitted: native RGB solid panels ({current.Sum(item => item.SubmissionCount)} builder calls)";
        }
        catch (Exception exception)
        {
            Hide();
            state = $"Stopped: {exception.Message}";
            DebugFileLog.Error(LogSource, exception, "Native opaque preview update failed");
        }
    }

    public void Dispose()
    {
        Hide();
        geometry?.Dispose();
        geometry = null;
    }

    private static bool TryCreatePreviewWorld(out Matrix4x4 world)
    {
        world = default;
        var control = Control.Instance();
        var camera = control == null ? null : control->CameraManager.GetActiveCamera();
        var device = Device.Instance();
        if (camera == null || device == null)
            return false;

        const float distance = 3f;
        var center = GetScreenRayPoint(camera, device->Width, device->Height, 0.5f, 0.48f, distance);
        var right = Vector3.Normalize(
            GetScreenRayPoint(camera, device->Width, device->Height, 0.6f, 0.48f, distance)
                - GetScreenRayPoint(camera, device->Width, device->Height, 0.4f, 0.48f, distance)
        );
        var up = Vector3.Normalize(
            GetScreenRayPoint(camera, device->Width, device->Height, 0.5f, 0.38f, distance)
                - GetScreenRayPoint(camera, device->Width, device->Height, 0.5f, 0.58f, distance)
        );
        var cameraPosition = new Vector3(camera->SceneCamera.Position.X, camera->SceneCamera.Position.Y, camera->SceneCamera.Position.Z);
        var forward = Vector3.Normalize(center - cameraPosition);
        world = new Matrix4x4(
            right.X * 0.5f,
            right.Y * 0.5f,
            right.Z * 0.5f,
            0f,
            up.X * 0.5f,
            up.Y * 0.5f,
            up.Z * 0.5f,
            0f,
            forward.X,
            forward.Y,
            forward.Z,
            0f,
            center.X,
            center.Y,
            center.Z,
            1f
        );
        return true;
    }

    private void CompleteDrawCapture(string reason)
    {
        if (!drawCaptureActive || underpaint == null)
            return;
        drawCaptureActive = false;
        underpaint.CompleteNativeGeometryDrawCapture(reason);
        if (!underpaint.TryTakeNativeGeometryDrawCapture(out var capture))
            return;

        var groups = capture
            .Draws.GroupBy(draw => $"{draw.Pass}/{draw.DrawType}/Count={draw.ElementCount}")
            .Select(group => $"{group.Key}:x{group.Count()}");
        DebugFileLog.Information(
            LogSource,
            "Native preview actual draw capture Reason={Reason} Draws={Draws} Groups={Groups}",
            capture.Reason,
            capture.Draws.Count,
            string.Join(",", groups)
        );
        foreach (var draw in capture.Draws.Where(draw => draw.Pass == "Opaque" && draw.DrawType == "DrawIndexed"))
        {
            DebugFileLog.Information(
                LogSource,
                "Native preview opaque draw Sequence={Sequence} VS={VS} PS={PS} Layout={Layout} VSCB={VSCB} PSCB={PSCB} SRV={SRV} State={State}",
                draw.Sequence,
                $"0x{draw.VertexShader:X}",
                $"0x{draw.PixelShader:X}",
                $"0x{draw.InputLayout:X}",
                string.Join(",", draw.VertexConstantBuffers.Select(FormatConstant)),
                string.Join(",", draw.PixelConstantBuffers.Select(FormatConstant)),
                string.Join(",", draw.ShaderResources.Select(FormatResource)),
                draw.PipelineState
            );
        }
        if (instances.Length != 0)
        {
            if (instances[0].TakeSelectedBindingMapCapture() is { } bindingMap)
                DebugFileLog.Information(LogSource, "Native preview selected resource map {BindingMap}", bindingMap);
            foreach (var submission in instances[0].TakeSubmissionCapture())
            {
                DebugFileLog.Information(
                    LogSource,
                    "Native preview builder {Phase} Sequence={Sequence} Frame={Frame} Context={Context} View={View}/{SubView} Thread={Thread} WorldCB={WorldCB} SourcePointer={SourcePointer} Flags={Flags} Hash={Hash} CurrentHash={CurrentHash} PreviousHash={PreviousHash} Equal={Equal} Current={Current} Previous={Previous}",
                    submission.Phase,
                    submission.Sequence,
                    submission.Frame,
                    $"0x{submission.Context:X}",
                    submission.View,
                    submission.SubView,
                    submission.ThreadId,
                    $"0x{submission.WorldConstant:X}",
                    $"0x{submission.SourcePointer:X}",
                    $"0x{submission.ConstantFlags:X}",
                    FormatHash(submission.ContentHash),
                    FormatHash(submission.CurrentHash),
                    FormatHash(submission.PreviousHash),
                    submission.CurrentHash == submission.PreviousHash,
                    FormatMatrix(submission.CurrentWorldView),
                    FormatMatrix(submission.PreviousWorldView)
                );
            }
        }
    }

    private static Matrix4x4 OffsetWorld(Matrix4x4 world, float rightOffset)
    {
        var right = Vector3.Normalize(new Vector3(world.M11, world.M12, world.M13));
        world.M41 += right.X * rightOffset;
        world.M42 += right.Y * rightOffset;
        world.M43 += right.Z * rightOffset;
        return world;
    }

    private static string FormatConstant(NativeGeometryConstantBufferBinding constant) =>
        $"{constant.Slot}:0x{constant.Buffer:X}/{constant.ByteWidth}/{FormatHash(constant.ContentHash)}/{FormatHash(constant.FirstHalfHash)}:{FormatHash(constant.SecondHalfHash)}";

    private static string FormatHash(ulong? hash) => hash is { } value ? $"{value:X16}" : "-";

    private static string FormatMatrix(Matrix4x4 value) =>
        $"[{value.M11:R},{value.M12:R},{value.M13:R},{value.M14:R};{value.M21:R},{value.M22:R},{value.M23:R},{value.M24:R};{value.M31:R},{value.M32:R},{value.M33:R},{value.M34:R};{value.M41:R},{value.M42:R},{value.M43:R},{value.M44:R}]";

    private static string FormatResource(NativeGeometryShaderResourceBinding resource) =>
        $"{resource.Slot}:0x{resource.View:X}/0x{resource.Resource:X}";

    private static Vector3 GetScreenRayPoint(
        FFXIVClientStructs.FFXIV.Client.Game.Camera* camera,
        uint viewportWidth,
        uint viewportHeight,
        float normalizedX,
        float normalizedY,
        float distance
    )
    {
        var point = new FFXIVClientStructs.FFXIV.Common.Math.Vector2(normalizedX * viewportWidth, normalizedY * viewportHeight);
        var ray = camera->SceneCamera.ScreenPointToRay(point);
        var origin = new Vector3(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
        var direction = Vector3.Normalize(new Vector3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z));
        return origin + direction * distance;
    }
}
#endif
