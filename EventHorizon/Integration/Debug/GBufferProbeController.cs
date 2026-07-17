using System.Threading;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using SharpDX;
using SharpDX.Direct3D11;
using Underpaint;
using D3D11Device = SharpDX.Direct3D11.Device;
using EventHorizonConfiguration = EventHorizon.Settings.Configuration;
using KernelDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace EventHorizon.Integration.Debug;

internal readonly record struct GBufferWorldMarker(string Label, Vector3 Center, Vector4 Color);

/// <summary>Feeds the EventHorizon test quad into Underpaint's opaque backend.</summary>
internal sealed unsafe class GBufferProbeController : IDisposable
{
    private readonly EventHorizonConfiguration configuration;
    private readonly UnderpaintRenderer? renderer;
    private readonly Lock stateLock = new();
    private readonly D3D11Device device;
    private readonly Texture2D testTexture;
    private readonly ShaderResourceView testTextureView;

    private bool lastEnabled;
    private volatile bool publishingEnabled = true;
    private bool disposed;
    private OpaqueQuad? quad;
    private GBufferWorldMarker? marker;
    private GBufferMaterial materialParameters = GBufferMaterial.Default;

    private readonly record struct OpaqueQuad(Vector3 Center, Vector3 Right, Vector3 Down);

    public GBufferProbeController(EventHorizonConfiguration configuration, UnderpaintRenderer? renderer)
    {
        this.configuration = configuration;
        this.renderer = renderer;

        var kernelDevice = KernelDevice.Instance();
        if (kernelDevice == null || kernelDevice->D3D11Forwarder == null)
        {
            throw new InvalidOperationException("D3D11 device is not available.");
        }

        device = new D3D11Device((nint)kernelDevice->D3D11Forwarder);
        (testTexture, testTextureView) = CreateTestTexture();
    }

    public GBufferMaterial MaterialParameters
    {
        get => materialParameters;
        set => materialParameters = value;
    }

    public void ResetMaterialParameters() => MaterialParameters = GBufferMaterial.Default;

    public bool PublishingEnabled
    {
        get => publishingEnabled;
        set => publishingEnabled = value;
    }

    public void Update()
    {
        if (!publishingEnabled)
        {
            return;
        }

        lock (stateLock)
        {
            var enabled = configuration.EnableGBufferProbe && renderer != null;
            if (lastEnabled != enabled)
            {
                lastEnabled = enabled;
                quad = null;
                marker = null;
                if (!enabled)
                {
                    renderer?.Clear(GBufferTarget.Opaque);
                }
            }

            if (!lastEnabled)
            {
                return;
            }

            if (!TryEnsureQuad(out var currentQuad))
            {
                renderer?.Clear(GBufferTarget.Opaque);
                return;
            }

            using var drawList = renderer!.DrawOpaque(materialParameters);
            drawList.AddImage(testTextureView.NativePointer, currentQuad.Center, currentQuad.Right, currentQuad.Down);
        }
    }

    public bool TryGetWorldMarker(out GBufferWorldMarker worldMarker)
    {
        lock (stateLock)
        {
            if (!publishingEnabled || !lastEnabled || marker is not { } currentMarker)
            {
                worldMarker = default;
                return false;
            }

            worldMarker = currentMarker;
            return true;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        renderer?.Clear(GBufferTarget.Opaque);
        testTextureView.Dispose();
        testTexture.Dispose();
    }

    private bool TryEnsureQuad(out OpaqueQuad currentQuad)
    {
        if (quad is { } existing)
        {
            currentQuad = existing;
            return true;
        }

        var control = Control.Instance();
        var activeCamera = control != null ? control->CameraManager.GetActiveCamera() : null;
        var kernelDevice = KernelDevice.Instance();
        if (activeCamera == null || kernelDevice == null)
        {
            currentQuad = default;
            return false;
        }

        var viewportWidth = kernelDevice->Width;
        var viewportHeight = kernelDevice->Height;
        var cameraPosition = new Vector3(
            activeCamera->SceneCamera.Position.X,
            activeCamera->SceneCamera.Position.Y,
            activeCamera->SceneCamera.Position.Z
        );
        var center = GetScreenRayPoint(activeCamera, viewportWidth, viewportHeight, 0.50f, 0.48f);
        var forward = Vector3.Normalize(center - cameraPosition);
        var screenRight = Vector3.Normalize(
            GetScreenRayPoint(activeCamera, viewportWidth, viewportHeight, 0.55f, 0.48f)
                - GetScreenRayPoint(activeCamera, viewportWidth, viewportHeight, 0.45f, 0.48f)
        );
        var screenUp = Vector3.Normalize(
            GetScreenRayPoint(activeCamera, viewportWidth, viewportHeight, 0.50f, 0.43f)
                - GetScreenRayPoint(activeCamera, viewportWidth, viewportHeight, 0.50f, 0.53f)
        );

        currentQuad = new OpaqueQuad(center, (screenRight * 2.5f) + (forward * 1.3f), screenUp * -1.8f);
        quad = currentQuad;
        marker = new GBufferWorldMarker("Underpaint opaque quad", center, new Vector4(1f, 0.1f, 1f, 1f));
        return true;
    }

    private (Texture2D Texture, ShaderResourceView View) CreateTestTexture()
    {
        const int size = 256;
        const int cells = 8;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var cellX = x * cells / size;
                var cellY = y * cells / size;
                var brightness = (cellX + cellY) % 2 == 0 ? 1f : 0.28f;
                var tint = (x >= size / 2, y >= size / 2) switch
                {
                    (false, false) => new Vector3(1f, 0.18f, 0.18f),
                    (true, false) => new Vector3(0.18f, 1f, 0.18f),
                    (false, true) => new Vector3(0.18f, 0.35f, 1f),
                    _ => new Vector3(1f, 0.82f, 0.18f),
                };
                var grid = x % (size / cells) < 2 || y % (size / cells) < 2;
                var color = grid ? Vector3.One : tint * brightness;
                var offset = ((y * size) + x) * 4;
                pixels[offset] = (byte)(Math.Clamp(color.X, 0f, 1f) * 255f);
                pixels[offset + 1] = (byte)(Math.Clamp(color.Y, 0f, 1f) * 255f);
                pixels[offset + 2] = (byte)(Math.Clamp(color.Z, 0f, 1f) * 255f);
                pixels[offset + 3] = 255;
            }
        }

        var description = new Texture2DDescription
        {
            Width = size,
            Height = size,
            MipLevels = 1,
            ArraySize = 1,
            Format = SharpDX.DXGI.Format.R8G8B8A8_UNorm,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        };
        fixed (byte* data = pixels)
        {
            var texture = new Texture2D(device, description, [new DataRectangle((nint)data, size * 4)]);
            return (texture, new ShaderResourceView(device, texture));
        }
    }

    private static Vector3 GetScreenRayPoint(
        FFXIVClientStructs.FFXIV.Client.Game.Camera* activeCamera,
        uint viewportWidth,
        uint viewportHeight,
        float normalizedX,
        float normalizedY
    )
    {
        var screenPoint = new FFXIVClientStructs.FFXIV.Common.Math.Vector2(normalizedX * viewportWidth, normalizedY * viewportHeight);
        var ray = activeCamera->SceneCamera.ScreenPointToRay(screenPoint);
        var origin = new Vector3(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
        var direction = Vector3.Normalize(new Vector3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z));
        return origin + (direction * 5f);
    }
}
