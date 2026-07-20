#if DEBUG
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace EventHorizon.Integration.Debug;

internal sealed unsafe class NativeBgObjectPreviewController : IDisposable
{
    private const string LogSource = "NativeBgObjectPreview";
    private const string ModelPath = "bgcommon/hou/indoor/general/0517/bgparts/fun_b0_m0517a.mdl";
    private const string PoolName = "EventHorizon.Underpaint.BgObject";
    private const byte LoadedResourceState = 7;

    private readonly object requestLock = new();
    private BgObject* bgObject;
    private Vector3 initialPosition;
    private bool createRequested;
    private bool destroyRequested;
    private bool? visibleRequested;
    private float moveRequested;
    private byte lastLoadState = byte.MaxValue;
    private bool renderInitialized;
    private string state = "Hidden";

    public bool IsPresent => bgObject != null || createRequested;
    public bool IsReady => bgObject != null && GetLoadState(bgObject) >= LoadedResourceState;
    public bool IsVisible => bgObject != null && bgObject->IsVisible;
    public string State => state;

    public void Show()
    {
        lock (requestLock)
        {
            if (bgObject != null || createRequested)
                return;
            createRequested = true;
            destroyRequested = false;
            state = "Waiting for framework-thread BgObject.Create";
        }
    }

    public void Hide()
    {
        lock (requestLock)
        {
            createRequested = false;
            destroyRequested = bgObject != null;
            state = destroyRequested ? "Waiting for native cleanup" : "Hidden";
        }
    }

    public void SetVisible(bool visible)
    {
        lock (requestLock)
            visibleRequested = visible;
    }

    public void MoveRight(float metres)
    {
        lock (requestLock)
            moveRequested += metres;
    }

    public void ResetPosition()
    {
        lock (requestLock)
            moveRequested = float.NaN;
    }

    public void Update()
    {
        try
        {
            ConsumeRequests();
            if (bgObject == null)
                return;

            var loadState = GetLoadState(bgObject);
            if (loadState != lastLoadState)
            {
                lastLoadState = loadState;
                DebugFileLog.Information(
                    LogSource,
                    "BgObject load state changed Object={Object} ModelResource={ModelResource} LoadState={LoadState}",
                    $"0x{(nint)bgObject:X}",
                    $"0x{(nint)bgObject->ModelResourceHandle:X}",
                    loadState
                );
            }

            if (loadState >= LoadedResourceState && !renderInitialized)
                InitializeLoadedRenderState();

            state =
                loadState >= LoadedResourceState
                    ? $"Ready: native BgObject 0x{(nint)bgObject:X}"
                    : $"Loading stock model (state {loadState})";
        }
        catch (Exception exception)
        {
            state = $"Stopped: {exception.Message}";
            DebugFileLog.Error(LogSource, exception, "Native BgObject preview stopped");
            DestroyNow();
        }
    }

    public void Dispose()
    {
        lock (requestLock)
        {
            createRequested = false;
            destroyRequested = false;
            visibleRequested = null;
            moveRequested = 0;
        }
        DestroyNow();
    }

    private void ConsumeRequests()
    {
        bool create;
        bool destroy;
        bool? visible;
        float move;
        lock (requestLock)
        {
            create = createRequested;
            destroy = destroyRequested;
            visible = visibleRequested;
            move = moveRequested;
            createRequested = false;
            destroyRequested = false;
            visibleRequested = null;
            moveRequested = 0;
        }

        if (destroy)
            DestroyNow();
        if (create && bgObject == null)
            CreateNow();
        if (bgObject == null)
            return;
        if (visible is { } isVisible)
            ApplyVisibility(isVisible);
        if (float.IsNaN(move))
            ApplyPosition(initialPosition, "reset");
        else if (move != 0)
        {
            var position = ToNumerics(bgObject->Position);
            ApplyPosition(position + Vector3.UnitX * move, $"move-x:{move:R}");
        }
    }

    private void CreateNow()
    {
        if (!TryGetPreviewPosition(out initialPosition))
            throw new InvalidOperationException("No active world camera is available.");

        bgObject = BgObject.Create(ModelPath, PoolName);
        if (bgObject == null)
            throw new InvalidOperationException("BgObject.Create returned null.");

        bgObject->Position = ToNative(initialPosition);
        bgObject->Rotation = FFXIVClientStructs.FFXIV.Common.Math.Quaternion.Identity;
        bgObject->Scale = new FFXIVClientStructs.FFXIV.Common.Math.Vector3(1f, 1f, 1f);
        bgObject->IsVisible = true;
        bgObject->NotifyTransformChanged();
        if (GetLoadState(bgObject) >= LoadedResourceState)
        {
            bgObject->UpdateTransforms(false);
            bgObject->UpdateCulling();
        }

        lastLoadState = byte.MaxValue;
        renderInitialized = false;
        DebugFileLog.Information(
            LogSource,
            "Created native BgObject Object={Object} Model={Model} Position={Position}; no custom pass-builder submission is active",
            $"0x{(nint)bgObject:X}",
            ModelPath,
            initialPosition
        );
    }

    private void ApplyVisibility(bool visible)
    {
        bgObject->IsVisible = visible;
        bgObject->UpdateRender();
        bgObject->UpdateCulling();
        DebugFileLog.Information(LogSource, "BgObject visibility changed Visible={Visible}", visible);
    }

    private void ApplyPosition(Vector3 position, string reason)
    {
        bgObject->Position = ToNative(position);
        bgObject->NotifyTransformChanged();
        if (GetLoadState(bgObject) >= LoadedResourceState)
            bgObject->UpdateTransforms(false);
        bgObject->UpdateCulling();
        DebugFileLog.Information(LogSource, "BgObject transform changed Reason={Reason} Position={Position}", reason, position);
    }

    private void DestroyNow()
    {
        var current = bgObject;
        bgObject = null;
        lastLoadState = byte.MaxValue;
        renderInitialized = false;
        if (current == null)
        {
            state = "Hidden";
            return;
        }

        var address = (nint)current;
        current->CleanupRender();
        current->Dtor(1);
        state = "Hidden";
        DebugFileLog.Information(LogSource, "Destroyed native BgObject Object={Object} via CleanupRender -> Dtor(1)", $"0x{address:X}");
    }

    private static byte GetLoadState(BgObject* value) =>
        value->ModelResourceHandle == null ? (byte)0 : value->ModelResourceHandle->LoadState;

    private void InitializeLoadedRenderState()
    {
        bgObject->NotifyTransformChanged();
        bgObject->UpdateTransforms(false);
        bgObject->UpdateRender();
        bgObject->UpdateCulling();
        renderInitialized = true;

        FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds bounds;
        var hasBounds = bgObject->ComputeAxisAlignedBounds(&bounds) != null;
        DebugFileLog.Information(
            LogSource,
            "BgObject render state initialized Object={Object} Bounds={Bounds}",
            $"0x{(nint)bgObject:X}",
            hasBounds ? $"{bounds.Min}..{bounds.Max}" : "unavailable"
        );
    }

    private static bool TryGetPreviewPosition(out Vector3 position)
    {
        position = default;
        var control = Control.Instance();
        var camera = control == null ? null : control->CameraManager.GetActiveCamera();
        var device = Device.Instance();
        if (camera == null || device == null)
            return false;

        var screen = new FFXIVClientStructs.FFXIV.Common.Math.Vector2(device->Width * 0.5f, device->Height * 0.5f);
        var ray = camera->SceneCamera.ScreenPointToRay(screen);
        var origin = new Vector3(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
        var direction = Vector3.Normalize(new Vector3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z));
        position = origin + direction * 4f;
        return true;
    }

    private static FFXIVClientStructs.FFXIV.Common.Math.Vector3 ToNative(Vector3 value) => new(value.X, value.Y, value.Z);

    private static Vector3 ToNumerics(FFXIVClientStructs.FFXIV.Common.Math.Vector3 value) => new(value.X, value.Y, value.Z);
}
#endif
