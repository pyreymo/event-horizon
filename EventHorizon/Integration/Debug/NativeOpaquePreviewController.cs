#if DEBUG
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Underpaint;
using Underpaint.Internal;

namespace EventHorizon.Integration.Debug;

internal sealed unsafe class NativeOpaquePreviewController : IDisposable
{
    private const string LogSource = "NativeOpaquePreview";
    private readonly UnderpaintRenderer? underpaint;
    private NativeGeometry? geometry;
    private NativeRigidInstance? instance;
    private string state = "Hidden";

    public NativeOpaquePreviewController(UnderpaintRenderer? underpaint)
    {
        this.underpaint = underpaint;
    }

    public bool IsVisible => instance != null;
    public string State => state;

    public void Show()
    {
        if (instance != null)
            return;
        if (underpaint == null)
        {
            state = "Unavailable: Underpaint failed to initialize";
            return;
        }
        if (!TryGetCameraFacingWorld(out var world))
        {
            state = "Unavailable: no active world camera";
            return;
        }

        geometry ??= underpaint.CreateNativeGeometry(
            [new(-0.8f, -0.8f, 0f), new(0.8f, -0.8f, 0f), new(0.8f, 0.8f, 0f), new(-0.8f, 0.8f, 0f)],
            [new(0f, 1f), new(1f, 1f), new(1f, 0f), new(0f, 0f)],
            [0, 1, 2, 2, 3, 0, 2, 1, 0, 0, 3, 2]
        );
        instance = underpaint.CreateNativeRigidInstance(geometry, world);
        state = "Visible: camera-facing native opaque quad";
        DebugFileLog.Information(LogSource, "Native opaque preview shown");
    }

    public void Hide()
    {
        var current = instance;
        instance = null;
        current?.Dispose();
        state = "Hidden";
        if (current != null)
            DebugFileLog.Information(LogSource, "Native opaque preview hidden");
    }

    public void Update()
    {
        var current = instance;
        if (current == null)
            return;
        if (current.Failure is { } failure)
        {
            Hide();
            state = $"Stopped: {failure}";
            return;
        }
        if (!TryGetCameraFacingWorld(out var world))
            return;
        try
        {
            current.UpdateWorldView(world);
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

    private static bool TryGetCameraFacingWorld(out Matrix4x4 world)
    {
        world = default;
        var manager = CameraManager.Instance();
        var camera = manager == null ? null : manager->CurrentCamera;
        if (camera == null || camera->RenderCamera == null)
            return false;

        var view = *(Matrix4x4*)&camera->ViewMatrix;
        var projection = *(Matrix4x4*)&camera->RenderCamera->ProjectionMatrix;
        if (!Matrix4x4.Invert(view, out var cameraWorld))
            return false;

        for (var index = 0; index < 2; index++)
        {
            var depth = index == 0 ? 3f : -3f;
            var candidate = Matrix4x4.CreateTranslation(0f, 0f, depth) * cameraWorld;
            var center = Vector3.Transform(Vector3.Zero, candidate);
            var clip = Vector4.Transform(new Vector4(center, 1f), view * projection);
            if (clip.W <= 0.001f)
                continue;
            var inverseW = 1f / clip.W;
            var x = clip.X * inverseW;
            var y = clip.Y * inverseW;
            var z = clip.Z * inverseW;
            if (MathF.Abs(x) > 0.8f || MathF.Abs(y) > 0.8f || z is < 0f or > 1f)
                continue;
            world = candidate;
            return true;
        }
        return false;
    }
}
#endif
