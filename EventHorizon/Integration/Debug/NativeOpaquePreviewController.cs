#if DEBUG
using System.Numerics;
using Underpaint;
using Underpaint.Internal;

namespace EventHorizon.Integration.Debug;

internal sealed class NativeOpaquePreviewController : IDisposable
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
        instance = underpaint.CreateNativeRigidInstance(geometry, Matrix4x4.CreateTranslation(0f, 0f, 5f));
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
}
#endif
