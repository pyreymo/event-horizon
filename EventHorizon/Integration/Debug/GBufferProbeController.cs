using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using D3D11Buffer = SharpDX.Direct3D11.Buffer;
using D3D11Device = SharpDX.Direct3D11.Device;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using EventHorizonConfiguration = EventHorizon.Settings.Configuration;

namespace EventHorizon.Integration.Debug;

internal readonly record struct GBufferWorldMarker(string Label, System.Numerics.Vector3 Center, System.Numerics.Vector4 Color);

internal readonly record struct GBufferMaterialParameters(
    System.Numerics.Vector4 G0,
    System.Numerics.Vector4 G1,
    System.Numerics.Vector4 G2,
    System.Numerics.Vector4 G3,
    System.Numerics.Vector4 G4,
    byte Stencil
)
{
    public static GBufferMaterialParameters Default =>
        new(
            new System.Numerics.Vector4(0.5f, 1f, 0.5f, 128f / 255f),
            new System.Numerics.Vector4(243f / 255f, 216f / 255f, 0f, 0f),
            new System.Numerics.Vector4(52f / 255f, 42f / 255f, 35f / 255f, 1f),
            new System.Numerics.Vector4(65504f, 0f, 0f, 1f),
            new System.Numerics.Vector4(127f / 255f, 1f, 127f / 255f, 0f),
            0x10
        );
}

/// <summary>
/// Experimental textured opaque renderer. It writes a plugin-owned world quad into the
/// five main G-buffers and scene depth using a runtime-adjustable material tuple.
/// </summary>
internal sealed unsafe class GBufferProbeController : IDisposable
{
    private readonly EventHorizonConfiguration configuration;
    private readonly IPluginLog log;
    private readonly object stateLock = new();
    private readonly nint immediateContextPointer;
    private readonly D3D11Device device;
    private readonly DeviceContext immediateContext;
    private readonly DeviceContext deferredContext;

    private readonly D3D11Buffer vertexBuffer;
    private readonly D3D11Buffer constantsBuffer;
    private readonly InputLayout inputLayout;
    private readonly VertexShader vertexShader;
    private readonly PixelShader pixelShader;
    private readonly Texture2D testTexture;
    private readonly ShaderResourceView testTextureView;
    private readonly SamplerState testSampler;
    private readonly RasterizerState rasterizerState;
    private readonly BlendState blendState;
    private readonly DepthStencilState depthStencilState;

    private readonly Hook<OMSetRenderTargetsDelegate> omSetRenderTargetsHook;
    private readonly Hook<OMSetRenderTargetsAndUnorderedAccessViewsDelegate> omSetRenderTargetsAndUavsHook;

    private bool candidateActive;
    private nint[] candidateRenderTargets = [];
    private nint candidateDepthStencil;
    private uint candidateWidth;
    private uint candidateHeight;
    private bool detouring;
    private bool lastEnabled;
    private bool disposed;
    private WorldVertex[]? quadVertices;
    private GBufferWorldMarker? quadMarker;
    private GBufferMaterialParameters materialParameters = GBufferMaterialParameters.Default;

    [StructLayout(LayoutKind.Sequential)]
    private struct WorldVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TexCoord;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WorldConstants
    {
        public Matrix ViewProjection;
        public Vector4 G0;
        public Vector4 G1;
        public Vector4 G2;
        public Vector4 G3;
        public Vector4 G4;
    }

    private sealed class PendingProbe : IDisposable
    {
        public RenderTargetView[] RenderTargets { get; }
        public DepthStencilView DepthStencil { get; }
        public uint Width { get; }
        public uint Height { get; }

        public PendingProbe(nint[] renderTargets, nint depthStencil, uint width, uint height)
        {
            var retainedTargets = new List<RenderTargetView>(5);
            DepthStencilView? retainedDepth = null;
            try
            {
                foreach (var pointer in renderTargets.Take(5))
                {
                    Marshal.AddRef(pointer);
                    retainedTargets.Add(new RenderTargetView(pointer));
                }

                Marshal.AddRef(depthStencil);
                retainedDepth = new DepthStencilView(depthStencil);
            }
            catch
            {
                retainedDepth?.Dispose();
                foreach (var target in retainedTargets)
                {
                    target.Dispose();
                }
                throw;
            }

            RenderTargets = [.. retainedTargets];
            DepthStencil = retainedDepth;
            Width = width;
            Height = height;
        }

        public void Dispose()
        {
            DepthStencil.Dispose();
            foreach (var target in RenderTargets)
            {
                target.Dispose();
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsDelegate(nint context, uint numViews, nint* renderTargetViews, nint depthStencilView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsAndUnorderedAccessViewsDelegate(
        nint context,
        uint numRenderTargetViews,
        nint* renderTargetViews,
        nint depthStencilView,
        uint unorderedAccessViewStartSlot,
        uint numUnorderedAccessViews,
        nint* unorderedAccessViews,
        uint* unorderedAccessViewInitialCounts
    );

    public GBufferProbeController(IGameInteropProvider gameInteropProvider, EventHorizonConfiguration configuration, IPluginLog log)
    {
        this.configuration = configuration;
        this.log = log;

        var kernelDevice = Device.Instance();
        if (kernelDevice == null || kernelDevice->D3D11Forwarder == null || kernelDevice->D3D11DeviceContext == null)
        {
            throw new InvalidOperationException("D3D11 device or immediate context is not available.");
        }

        immediateContextPointer = (nint)kernelDevice->D3D11DeviceContext;
        device = new D3D11Device((nint)kernelDevice->D3D11Forwarder);
        immediateContext = device.ImmediateContext;
        deferredContext = new DeviceContext(device);

        const string shaderSource = """
            cbuffer WorldConstants : register(b0)
            {
                float4x4 ViewProjection;
                float4 MaterialG0;
                float4 MaterialG1;
                float4 MaterialG2;
                float4 MaterialG3;
                float4 MaterialG4;
            };

            Texture2D<float4> AlbedoTexture : register(t0);
            SamplerState AlbedoSampler : register(s0);

            struct VertexInput
            {
                float3 Position : POSITION;
                float3 Normal : NORMAL;
                float2 TexCoord : TEXCOORD0;
            };

            struct PixelInput
            {
                float4 Position : SV_POSITION;
                float3 WorldNormal : NORMAL;
                float2 TexCoord : TEXCOORD0;
            };

            PixelInput VS(VertexInput input)
            {
                PixelInput output;
                output.Position = mul(float4(input.Position, 1.0), ViewProjection);
                output.WorldNormal = input.Normal;
                output.TexCoord = input.TexCoord;
                return output;
            }

            struct GBufferOutput
            {
                float4 Target0 : SV_Target0;
                float4 Target1 : SV_Target1;
                float4 Target2 : SV_Target2;
                float4 Target3 : SV_Target3;
                float4 Target4 : SV_Target4;
            };

            GBufferOutput PS(PixelInput input)
            {
                GBufferOutput output;
                output.Target0 = MaterialG0;
                output.Target1 = MaterialG1;
                output.Target2 = MaterialG2;
                output.Target3 = MaterialG3;
                output.Target4 = MaterialG4;
                output.Target0.rgb = normalize(input.WorldNormal) * 0.5 + 0.5;
                output.Target2.rgb = AlbedoTexture.Sample(AlbedoSampler, input.TexCoord).rgb;
                return output;
            }
            """;

        using var compiledVertexShader = ShaderBytecode.Compile(shaderSource, "VS", "vs_5_0");
        using var compiledPixelShader = ShaderBytecode.Compile(shaderSource, "PS", "ps_5_0");
        vertexShader = new VertexShader(device, compiledVertexShader.Bytecode);
        pixelShader = new PixelShader(device, compiledPixelShader.Bytecode);
        inputLayout = new InputLayout(
            device,
            compiledVertexShader.Bytecode,
            [
                new InputElement("POSITION", 0, SharpDX.DXGI.Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, SharpDX.DXGI.Format.R32G32B32_Float, sizeof(float) * 3, 0),
                new InputElement("TEXCOORD", 0, SharpDX.DXGI.Format.R32G32_Float, sizeof(float) * 6, 0),
            ]
        );
        vertexBuffer = new D3D11Buffer(
            device,
            Utilities.SizeOf<WorldVertex>() * 6,
            ResourceUsage.Default,
            BindFlags.VertexBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0
        );
        constantsBuffer = new D3D11Buffer(
            device,
            Utilities.SizeOf<WorldConstants>(),
            ResourceUsage.Default,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0
        );

        (testTexture, testTextureView) = CreateTestTexture();
        testSampler = new SamplerState(
            device,
            new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = Comparison.Never,
                MaximumAnisotropy = 1,
                MinimumLod = 0,
                MaximumLod = 0,
                BorderColor = Color.Transparent,
            }
        );

        var rasterizerDescription = RasterizerStateDescription.Default();
        rasterizerDescription.CullMode = CullMode.None;
        rasterizerDescription.IsScissorEnabled = false;
        rasterizerState = new RasterizerState(device, rasterizerDescription);
        blendState = CreateFullWriteBlendState();

        var depthDescription = DepthStencilStateDescription.Default();
        depthDescription.IsDepthEnabled = true;
        depthDescription.DepthWriteMask = DepthWriteMask.All;
        depthDescription.DepthComparison = Comparison.GreaterEqual;
        depthDescription.IsStencilEnabled = true;
        depthDescription.StencilReadMask = 0xFF;
        depthDescription.StencilWriteMask = 0xFF;
        depthDescription.FrontFace.Comparison = Comparison.Always;
        depthDescription.FrontFace.FailOperation = StencilOperation.Keep;
        depthDescription.FrontFace.DepthFailOperation = StencilOperation.Keep;
        depthDescription.FrontFace.PassOperation = StencilOperation.Replace;
        depthDescription.BackFace = depthDescription.FrontFace;
        depthStencilState = new DepthStencilState(device, depthDescription);

        var vtable = *(nint**)immediateContextPointer;
        omSetRenderTargetsHook = gameInteropProvider.HookFromAddress<OMSetRenderTargetsDelegate>(vtable[33], OMSetRenderTargetsDetour);
        omSetRenderTargetsAndUavsHook = gameInteropProvider.HookFromAddress<OMSetRenderTargetsAndUnorderedAccessViewsDelegate>(
            vtable[34],
            OMSetRenderTargetsAndUavsDetour
        );
    }

    public void Enable()
    {
        omSetRenderTargetsHook.Enable();
        omSetRenderTargetsAndUavsHook.Enable();
    }

    public void Update()
    {
        if (lastEnabled == configuration.EnableGBufferProbe)
        {
            return;
        }

        lock (stateLock)
        {
            lastEnabled = configuration.EnableGBufferProbe;
            ResetCandidate();
            quadVertices = null;
            quadMarker = null;
            log.Information($"Textured opaque G-buffer probe {(lastEnabled ? "enabled" : "disabled")}.");
        }
    }

    public GBufferMaterialParameters MaterialParameters
    {
        get
        {
            lock (stateLock)
            {
                return materialParameters;
            }
        }
        set
        {
            lock (stateLock)
            {
                materialParameters = value;
            }
        }
    }

    public void ResetMaterialParameters() => MaterialParameters = GBufferMaterialParameters.Default;

    public bool TryGetWorldMarker(out GBufferWorldMarker marker)
    {
        lock (stateLock)
        {
            if (!configuration.EnableGBufferProbe || quadMarker is not { } currentMarker)
            {
                marker = default;
                return false;
            }

            marker = currentMarker;
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
        omSetRenderTargetsAndUavsHook.Dispose();
        omSetRenderTargetsHook.Dispose();

        lock (stateLock)
        {
            depthStencilState.Dispose();
            blendState.Dispose();
            rasterizerState.Dispose();
            testSampler.Dispose();
            testTextureView.Dispose();
            testTexture.Dispose();
            constantsBuffer.Dispose();
            vertexBuffer.Dispose();
            inputLayout.Dispose();
            pixelShader.Dispose();
            vertexShader.Dispose();
            deferredContext.Dispose();
            immediateContext.Dispose();
        }
    }

    private void OMSetRenderTargetsDetour(nint context, uint numViews, nint* renderTargetViews, nint depthStencilView)
    {
        PendingProbe? pendingProbe = null;
        try
        {
            if (!detouring && context == immediateContextPointer)
            {
                lock (stateLock)
                {
                    pendingProbe = TrackTargetChange(numViews, renderTargetViews, depthStencilView);
                }
            }
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Failed to track G-buffer render targets.");
        }

        try
        {
            omSetRenderTargetsHook.Original(context, numViews, renderTargetViews, depthStencilView);
        }
        catch
        {
            pendingProbe?.Dispose();
            throw;
        }

        IssueProbeSafely(pendingProbe);
    }

    private void OMSetRenderTargetsAndUavsDetour(
        nint context,
        uint numRenderTargetViews,
        nint* renderTargetViews,
        nint depthStencilView,
        uint unorderedAccessViewStartSlot,
        uint numUnorderedAccessViews,
        nint* unorderedAccessViews,
        uint* unorderedAccessViewInitialCounts
    )
    {
        PendingProbe? pendingProbe = null;
        try
        {
            if (!detouring && context == immediateContextPointer && numRenderTargetViews != uint.MaxValue)
            {
                lock (stateLock)
                {
                    pendingProbe = TrackTargetChange(numRenderTargetViews, renderTargetViews, depthStencilView);
                }
            }
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Failed to track G-buffer render targets with UAVs.");
        }

        try
        {
            omSetRenderTargetsAndUavsHook.Original(
                context,
                numRenderTargetViews,
                renderTargetViews,
                depthStencilView,
                unorderedAccessViewStartSlot,
                numUnorderedAccessViews,
                unorderedAccessViews,
                unorderedAccessViewInitialCounts
            );
        }
        catch
        {
            pendingProbe?.Dispose();
            throw;
        }

        IssueProbeSafely(pendingProbe);
    }

    private PendingProbe? TrackTargetChange(uint numViews, nint* renderTargetViews, nint depthStencilView)
    {
        if (!configuration.EnableGBufferProbe)
        {
            ResetCandidate();
            return null;
        }

        var match = MatchFullGBuffers(numViews, renderTargetViews);
        var isCandidate = depthStencilView != 0 && match.MatchedCount == 5;
        PendingProbe? pendingProbe = null;

        if (candidateActive && !isCandidate)
        {
            if (candidateRenderTargets.Length >= 5 && candidateDepthStencil != 0)
            {
                pendingProbe = new PendingProbe(candidateRenderTargets, candidateDepthStencil, candidateWidth, candidateHeight);
            }
            ResetCandidate();
        }

        if (!isCandidate)
        {
            return pendingProbe;
        }

        candidateActive = true;
        candidateDepthStencil = depthStencilView;
        candidateWidth = match.Width;
        candidateHeight = match.Height;
        candidateRenderTargets = new nint[5];
        for (var index = 0; index < 5; index++)
        {
            candidateRenderTargets[index] = renderTargetViews[index];
        }

        return pendingProbe;
    }

    private void IssueProbeSafely(PendingProbe? probe)
    {
        if (probe == null)
        {
            return;
        }

        try
        {
            lock (stateLock)
            {
                if (configuration.EnableGBufferProbe)
                {
                    IssueProbe(probe);
                }
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "Failed to draw the textured opaque G-buffer probe.");
        }
        finally
        {
            probe.Dispose();
        }
    }

    private void IssueProbe(PendingProbe probe)
    {
        if (!TryUpdateQuad(probe.Width, probe.Height, out var viewProjection))
        {
            return;
        }

        try
        {
            detouring = true;
            deferredContext.ClearState();

            deferredContext.OutputMerger.SetTargets(probe.DepthStencil, probe.RenderTargets);
            deferredContext.OutputMerger.SetBlendState(blendState);
            deferredContext.OutputMerger.SetDepthStencilState(depthStencilState, materialParameters.Stencil);
            deferredContext.Rasterizer.SetViewport(0, 0, probe.Width, probe.Height, 0, 1);
            deferredContext.Rasterizer.State = rasterizerState;
            deferredContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            deferredContext.InputAssembler.InputLayout = inputLayout;
            deferredContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vertexBuffer, Utilities.SizeOf<WorldVertex>(), 0));
            deferredContext.HullShader.Set(null);
            deferredContext.DomainShader.Set(null);
            deferredContext.GeometryShader.Set(null);
            deferredContext.VertexShader.Set(vertexShader);
            deferredContext.VertexShader.SetConstantBuffer(0, constantsBuffer);
            deferredContext.PixelShader.Set(pixelShader);
            deferredContext.PixelShader.SetConstantBuffer(0, constantsBuffer);
            deferredContext.PixelShader.SetShaderResource(0, testTextureView);
            deferredContext.PixelShader.SetSampler(0, testSampler);

            viewProjection.Transpose();
            var constants = new WorldConstants
            {
                ViewProjection = viewProjection,
                G0 = ToSharpDx(materialParameters.G0),
                G1 = ToSharpDx(materialParameters.G1),
                G2 = ToSharpDx(materialParameters.G2),
                G3 = ToSharpDx(materialParameters.G3),
                G4 = ToSharpDx(materialParameters.G4),
            };
            deferredContext.UpdateSubresource(ref constants, constantsBuffer);
            deferredContext.Draw(6, 0);

            using var commandList = deferredContext.FinishCommandList(false);
            immediateContext.ExecuteCommandList(commandList, true);
        }
        finally
        {
            detouring = false;
            deferredContext.ClearState();
        }
    }

    private bool TryUpdateQuad(uint viewportWidth, uint viewportHeight, out Matrix viewProjection)
    {
        viewProjection = default;
        var control = Control.Instance();
        var activeCamera = control != null ? control->CameraManager.GetActiveCamera() : null;
        if (control == null || activeCamera == null)
        {
            return false;
        }

        viewProjection = *(Matrix*)&control->ViewProjectionMatrix;
        if (quadVertices != null)
        {
            return true;
        }

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
        var horizontal = screenRight * 1.25f + forward * 0.65f;
        var vertical = screenUp * 0.9f;
        var bottomLeft = center - horizontal - vertical;
        var bottomRight = center + horizontal - vertical;
        var topLeft = center - horizontal + vertical;
        var topRight = center + horizontal + vertical;
        var normal = Vector3.Normalize(Vector3.Cross(bottomRight - bottomLeft, topLeft - bottomLeft));
        if (Vector3.Dot(normal, cameraPosition - center) < 0)
        {
            normal = -normal;
        }

        quadVertices =
        [
            new WorldVertex
            {
                Position = bottomLeft,
                Normal = normal,
                TexCoord = new Vector2(0f, 1f),
            },
            new WorldVertex
            {
                Position = bottomRight,
                Normal = normal,
                TexCoord = new Vector2(1f, 1f),
            },
            new WorldVertex
            {
                Position = topLeft,
                Normal = normal,
                TexCoord = new Vector2(0f, 0f),
            },
            new WorldVertex
            {
                Position = topLeft,
                Normal = normal,
                TexCoord = new Vector2(0f, 0f),
            },
            new WorldVertex
            {
                Position = bottomRight,
                Normal = normal,
                TexCoord = new Vector2(1f, 1f),
            },
            new WorldVertex
            {
                Position = topRight,
                Normal = normal,
                TexCoord = new Vector2(1f, 0f),
            },
        ];
        deferredContext.UpdateSubresource(quadVertices, vertexBuffer);
        quadMarker = new GBufferWorldMarker(
            "Textured opaque quad",
            new System.Numerics.Vector3(center.X, center.Y, center.Z),
            new System.Numerics.Vector4(1f, 0.1f, 1f, 1f)
        );
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
                var offset = (y * size + x) * 4;
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

    private BlendState CreateFullWriteBlendState()
    {
        var description = BlendStateDescription.Default();
        description.IndependentBlendEnable = true;
        for (var index = 0; index < 5; index++)
        {
            description.RenderTarget[index].RenderTargetWriteMask = ColorWriteMaskFlags.All;
        }
        return new BlendState(device, description);
    }

    private static (int MatchedCount, uint Width, uint Height) MatchFullGBuffers(uint numViews, nint* renderTargetViews)
    {
        var manager = RenderTargetManager.Instance();
        if (manager == null || renderTargetViews == null || numViews < 5)
        {
            return default;
        }

        var matchedCount = 0;
        uint width = 0;
        uint height = 0;
        for (var index = 0; index < 5; index++)
        {
            var texture = manager->GBuffers[index].Value;
            if (texture == null || texture->MipRenderTargets == null)
            {
                continue;
            }

            var expectedView = (nint)texture->MipRenderTargets->D3D11RenderTargetViewOrDepthStencilView;
            if (expectedView == 0 || renderTargetViews[index] != expectedView)
            {
                continue;
            }

            matchedCount++;
            if (width == 0)
            {
                width = texture->ActualWidth;
                height = texture->ActualHeight;
            }
        }

        return (matchedCount, width, height);
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
        return origin + direction * 5f;
    }

    private void ResetCandidate()
    {
        candidateActive = false;
        candidateRenderTargets = [];
        candidateDepthStencil = 0;
        candidateWidth = 0;
        candidateHeight = 0;
    }

    private static Vector4 ToSharpDx(System.Numerics.Vector4 value) => new(value.X, value.Y, value.Z, value.W);
}
