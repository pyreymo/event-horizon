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
        geometry ??= underpaint.CreateNativeGeometry(
            [new(-0.8f, -0.8f, 0f), new(0.8f, -0.8f, 0f), new(0.8f, 0.8f, 0f), new(-0.8f, 0.8f, 0f)],
            [new(0f, 1f), new(1f, 1f), new(1f, 0f), new(0f, 0f)],
            [0, 1, 2, 2, 3, 0, 2, 1, 0, 0, 3, 2]
        );
        var worldView = TryGetPreviewWorldView(out var currentWorldView) ? currentWorldView : Matrix4x4.CreateTranslation(0f, 0f, 1f);
        instance = underpaint.CreateNativeRigidInstance(geometry, worldView);
        state = "Waiting for the native render rendezvous";
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
        try
        {
            if (TryGetPreviewWorldView(out var worldView))
                current.UpdateWorldView(worldView);
            if (current.HasSubmitted)
                state = "Submitted: camera-facing native opaque quad";
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

    private static bool TryGetPreviewWorldView(out Matrix4x4 worldView)
    {
        worldView = default;
        var control = Control.Instance();
        var camera = control == null ? null : control->CameraManager.GetActiveCamera();
        var device = Device.Instance();
        if (camera == null || device == null)
            return false;

        var center = GetScreenRayPoint(camera, device->Width, device->Height, 0.5f, 0.48f);
        var right = Vector3.Normalize(
            GetScreenRayPoint(camera, device->Width, device->Height, 0.6f, 0.48f)
                - GetScreenRayPoint(camera, device->Width, device->Height, 0.4f, 0.48f)
        );
        var up = Vector3.Normalize(
            GetScreenRayPoint(camera, device->Width, device->Height, 0.5f, 0.38f)
                - GetScreenRayPoint(camera, device->Width, device->Height, 0.5f, 0.58f)
        );
        var cameraPosition = new Vector3(camera->SceneCamera.Position.X, camera->SceneCamera.Position.Y, camera->SceneCamera.Position.Z);
        var forward = Vector3.Normalize(center - cameraPosition);
        var world = new Matrix4x4(
            right.X * 1.5f,
            right.Y * 1.5f,
            right.Z * 1.5f,
            0f,
            up.X,
            up.Y,
            up.Z,
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
        var view = *(Matrix4x4*)&camera->SceneCamera.ViewMatrix;
        worldView = world * view;
        return true;
    }

    private static Vector3 GetScreenRayPoint(
        FFXIVClientStructs.FFXIV.Client.Game.Camera* camera,
        uint viewportWidth,
        uint viewportHeight,
        float normalizedX,
        float normalizedY
    )
    {
        var point = new FFXIVClientStructs.FFXIV.Common.Math.Vector2(normalizedX * viewportWidth, normalizedY * viewportHeight);
        var ray = camera->SceneCamera.ScreenPointToRay(point);
        var origin = new Vector3(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
        var direction = Vector3.Normalize(new Vector3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z));
        return origin + direction;
    }
}
#endif
