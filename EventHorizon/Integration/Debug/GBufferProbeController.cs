using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

internal readonly record struct GBufferWorldTriangleMarker(string Label, System.Numerics.Vector3 Center, System.Numerics.Vector4 Color);

/// <summary>
/// Experimental opaque G-buffer probe. It recognizes a pass by matching the currently bound
/// RTVs against RenderTargetManager.GBuffers, then inserts one small draw immediately before
/// that target set is replaced.
/// </summary>
internal sealed unsafe class GBufferProbeController : IDisposable
{
    private const int ProbeSize = 1024;
    private const int MinimumMatchedGBuffers = 3;
    private const int DetailedExitLogLimit = 1;
    private static readonly int[] DonorSweepDrawOrdinals = [306, 307, 378, 379, 433, 434];
    internal static readonly System.Numerics.Vector2 DonorSampleNormalized = new(0.25f, 0.75f);

    private readonly EventHorizonConfiguration configuration;
    private readonly IPluginLog log;
    private readonly nint immediateContextPointer;
    private readonly D3D11Device device;
    private readonly DeviceContext immediateContext;
    private readonly DeviceContext deferredContext;
    private readonly D3D11Buffer vertexBuffer;
    private readonly InputLayout inputLayout;
    private readonly VertexShader vertexShader;
    private readonly PixelShader pixelShader;
    private readonly D3D11Buffer probeConstantsBuffer;
    private readonly D3D11Buffer worldVertexBuffer;
    private readonly InputLayout worldInputLayout;
    private readonly VertexShader worldVertexShader;
    private readonly PixelShader worldPixelShader;
    private readonly D3D11Buffer worldConstantsBuffer;
    private readonly Texture2D materialTestTexture;
    private readonly ShaderResourceView materialTestTextureView;
    private readonly SamplerState materialTestSampler;
    private readonly RasterizerState rasterizerState;
    private readonly RasterizerState worldRasterizerState;
    private readonly BlendState depthOnlyBlendState;
    private readonly BlendState target0RgbBlendState;
    private readonly BlendState target2RgbBlendState;
    private readonly BlendState worldTriangleBlendState;
    private readonly BlendState fullGBufferBlendState;
    private readonly DepthStencilState depthWriteState;
    private readonly DepthStencilState worldDepthWriteState;
    private readonly DepthStencilState donorStencilWriteState;
    private readonly DepthStencilState noDepthWriteState;
    private readonly object probeStateLock = new();

    private readonly Hook<OMSetRenderTargetsDelegate> omSetRenderTargetsHook;
    private readonly Hook<OMSetRenderTargetsAndUnorderedAccessViewsDelegate> omSetRenderTargetsAndUavsHook;
    private readonly Hook<OMSetDepthStencilStateDelegate> omSetDepthStencilStateHook;
    private readonly Hook<SetShaderResourcesDelegate> psSetShaderResourcesHook;
    private readonly Hook<SetShaderResourcesDelegate> csSetShaderResourcesHook;
    private readonly Hook<DrawIndexedDelegate> drawIndexedHook;
    private readonly Hook<DrawDelegate> drawHook;
    private readonly Hook<DrawIndexedInstancedDelegate> drawIndexedInstancedHook;
    private readonly Hook<DrawInstancedDelegate> drawInstancedHook;
    private readonly Hook<ClearRenderTargetViewDelegate> clearRenderTargetViewHook;
    private readonly Hook<ClearDepthStencilViewDelegate> clearDepthStencilViewHook;

    private nint[] candidateRenderTargets = [];
    private nint candidateDepthStencil;
    private bool candidateActive;
    private int candidateDrawCount;
    private int candidateIndexedDrawCount;
    private int candidateOrdinal;
    private int nextCandidateOrdinal;
    private int completedCandidateExits;
    private uint candidateWidth;
    private uint candidateHeight;
    private long candidateStartTimestamp;
    private string candidateViewport = "unavailable";
    private string lastDepthStencilState = "unobserved";
    private string candidateDepthStencilState = "unobserved";
    private readonly List<string> candidateDepthStencilTransitions = [];
    private readonly List<string> candidateUavTransitions = [];
    private readonly Queue<string> targetBindingHistory = new();
    private readonly nint[] trackedGBufferResources = new nint[5];
    private readonly List<string> gBufferConsumerTransitions = [];
    private int drawsSinceTargetBinding;
    private long immediateDrawSerial;
    private long lastCandidateExitDrawSerial;
    private int gBufferConsumerSummaryLogCount;
    private bool probeIssuedSinceDepthClear;
    private bool detouring;
    private bool lastEnabled;
    private GBufferProbeMode lastMode;
    private int lastConfiguredOrdinal = -1;
    private int candidateLogCount;
    private int candidateExitLogCount;
    private int frameSummaryLogCount;
    private int injectionCount;
    private int donorSweepNextIndex;
    private int donorSweepInjectionLogCount;
    private bool disposed;
    private readonly long controllerStartTimestamp = Stopwatch.GetTimestamp();
    private WorldVertex[]? worldTriangleVertices;
    private GBufferWorldTriangleMarker[]? worldTriangleMarkers;
    private DonorSnapshot? donorSnapshot;
    private byte donorStencilReference = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeConstants
    {
        public float SplitX;
        private Vector3 padding;
    }

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
        public Vector4 Albedo;
        public Vector4 Diagnostic;
        public Vector4 MaterialOverride;
        public Vector4 MaterialMask;
        public Vector4 TextureControl;
    }

    private sealed class PendingProbe : IDisposable
    {
        public RenderTargetView?[] RenderTargets { get; }
        public DepthStencilView DepthStencil { get; }
        public uint Width { get; }
        public uint Height { get; }
        public int Ordinal { get; }
        public int DrawCount { get; }
        public int IndexedDrawCount { get; }
        public double StartMilliseconds { get; }
        public double EndMilliseconds { get; }
        public string Viewport { get; }
        public string NextTargets { get; }
        public string NativeDepthStencilState { get; }
        public string NativeDepthStencilTransitions { get; }

        public PendingProbe(
            nint[] renderTargetPointers,
            nint depthStencilPointer,
            uint width,
            uint height,
            int ordinal,
            int drawCount,
            int indexedDrawCount,
            double startMilliseconds,
            double endMilliseconds,
            string viewport,
            string nextTargets,
            string nativeDepthStencilState,
            string nativeDepthStencilTransitions
        )
        {
            var retainedTargets = new List<RenderTargetView?>(renderTargetPointers.Length);
            DepthStencilView? retainedDepth = null;
            try
            {
                foreach (var pointer in renderTargetPointers)
                {
                    if (pointer == 0)
                    {
                        retainedTargets.Add(null);
                        continue;
                    }

                    Marshal.AddRef(pointer);
                    retainedTargets.Add(new RenderTargetView(pointer));
                }

                Marshal.AddRef(depthStencilPointer);
                retainedDepth = new DepthStencilView(depthStencilPointer);
            }
            catch
            {
                retainedDepth?.Dispose();
                foreach (var target in retainedTargets)
                {
                    target?.Dispose();
                }
                throw;
            }

            RenderTargets = [.. retainedTargets];
            DepthStencil = retainedDepth;
            Width = width;
            Height = height;
            Ordinal = ordinal;
            DrawCount = drawCount;
            IndexedDrawCount = indexedDrawCount;
            StartMilliseconds = startMilliseconds;
            EndMilliseconds = endMilliseconds;
            Viewport = viewport;
            NextTargets = nextTargets;
            NativeDepthStencilState = nativeDepthStencilState;
            NativeDepthStencilTransitions = nativeDepthStencilTransitions;
        }

        public void Dispose()
        {
            DepthStencil.Dispose();
            foreach (var target in RenderTargets)
            {
                target?.Dispose();
            }
        }
    }

    private sealed class DonorSnapshot : IDisposable
    {
        public Texture2D[] Textures { get; }
        public ShaderResourceView[] Views { get; }
        public Texture2D[] StagingTextures { get; }
        public int[] BytesPerPixel { get; }
        public Texture2D DepthStencilStagingTexture { get; }
        public int DepthStencilBytesPerPixel { get; }
        public int SampleX { get; }
        public int SampleY { get; }
        public string DepthStencilFormat { get; }
        public string Formats { get; }
        public bool ReadbackPending { get; set; } = true;

        public DonorSnapshot(
            Texture2D[] textures,
            ShaderResourceView[] views,
            Texture2D[] stagingTextures,
            int[] bytesPerPixel,
            Texture2D depthStencilStagingTexture,
            int depthStencilBytesPerPixel,
            int sampleX,
            int sampleY,
            string depthStencilFormat,
            string formats
        )
        {
            Textures = textures;
            Views = views;
            StagingTextures = stagingTextures;
            BytesPerPixel = bytesPerPixel;
            DepthStencilStagingTexture = depthStencilStagingTexture;
            DepthStencilBytesPerPixel = depthStencilBytesPerPixel;
            SampleX = sampleX;
            SampleY = sampleY;
            DepthStencilFormat = depthStencilFormat;
            Formats = formats;
        }

        public void Dispose()
        {
            foreach (var view in Views)
            {
                view.Dispose();
            }

            foreach (var texture in Textures)
            {
                texture.Dispose();
            }

            foreach (var texture in StagingTextures)
            {
                texture.Dispose();
            }

            DepthStencilStagingTexture.Dispose();
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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetDepthStencilStateDelegate(nint context, nint depthStencilState, uint stencilReference);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetShaderResourcesDelegate(nint context, uint startSlot, uint numViews, nint* shaderResourceViews);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawIndexedDelegate(nint context, uint indexCount, uint startIndexLocation, int baseVertexLocation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawDelegate(nint context, uint vertexCount, uint startVertexLocation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawIndexedInstancedDelegate(
        nint context,
        uint indexCountPerInstance,
        uint instanceCount,
        uint startIndexLocation,
        int baseVertexLocation,
        uint startInstanceLocation
    );

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawInstancedDelegate(
        nint context,
        uint vertexCountPerInstance,
        uint instanceCount,
        uint startVertexLocation,
        uint startInstanceLocation
    );

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ClearRenderTargetViewDelegate(nint context, nint renderTargetView, float* color);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ClearDepthStencilViewDelegate(nint context, nint depthStencilView, uint clearFlags, float depth, byte stencil);

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
            struct VertexInput
            {
                float2 Position : POSITION;
            };

            struct PixelInput
            {
                float4 Position : SV_POSITION;
            };

            PixelInput VS(VertexInput input)
            {
                PixelInput output;
                output.Position = float4(input.Position, 0.5, 1.0);
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

            cbuffer ProbeConstants : register(b0)
            {
                float SplitX;
                float3 Padding;
            };

            GBufferOutput PS(PixelInput input)
            {
                bool leftHalf = input.Position.x < SplitX;
                GBufferOutput output;
                output.Target0 = float4(leftHalf ? float3(0.5, 1.0, 0.5) : float3(0.5, 0.0, 0.5), 0.0);
                output.Target1 = 0.0;
                output.Target2 = float4(leftHalf ? float3(0.25, 0.25, 0.25) : float3(0.75, 0.75, 0.75), 0.0);
                output.Target3 = 0.0;
                output.Target4 = 0.0;
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
            [new InputElement("POSITION", 0, SharpDX.DXGI.Format.R32G32_Float, 0, 0)]
        );
        vertexBuffer = D3D11Buffer.Create(device, BindFlags.VertexBuffer, [new Vector2(-1, -1), new Vector2(-1, 3), new Vector2(3, -1)]);
        probeConstantsBuffer = new D3D11Buffer(
            device,
            Utilities.SizeOf<ProbeConstants>(),
            ResourceUsage.Default,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0
        );

        const string worldShaderSource = """
            cbuffer WorldConstants : register(b0)
            {
                float4x4 ViewProjection;
                float4 Albedo;
                float4 Diagnostic;
                float4 MaterialOverride;
                float4 MaterialMask;
                float4 TextureControl;
            };

            Texture2D<float4> DonorG0 : register(t0);
            Texture2D<float4> DonorG1 : register(t1);
            Texture2D<float4> DonorG2 : register(t2);
            Texture2D<float4> DonorG3 : register(t3);
            Texture2D<float4> DonorG4 : register(t4);
            Texture2D<float4> TestAlbedo : register(t5);
            SamplerState TestSampler : register(s0);

            struct VertexInput
            {
                float3 Position : POSITION;
                float3 Normal : NORMAL;
                float2 TexCoord : TEXCOORD0;
                uint VertexId : SV_VertexID;
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
                if (Diagnostic.x > 0.5)
                {
                    const float2 clipPositions[3] =
                    {
                        float2(-0.12, 0.10),
                        float2( 0.12, 0.10),
                        float2( 0.00, 0.34)
                    };
                    output.Position = float4(clipPositions[input.VertexId % 3], 0.5, 1.0);
                }
                else
                {
                    output.Position = mul(float4(input.Position, 1.0), ViewProjection);
                }
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
                if (Diagnostic.y > 0.5)
                {
                    output.Target0 = DonorG0.Load(int3(0, 0, 0));
                    output.Target1 = DonorG1.Load(int3(0, 0, 0));
                    output.Target2 = DonorG2.Load(int3(0, 0, 0));
                    output.Target3 = DonorG3.Load(int3(0, 0, 0));
                    output.Target4 = DonorG4.Load(int3(0, 0, 0));
                    if (Diagnostic.z < 0.5)
                    {
                        output.Target0.rgb = normalize(input.WorldNormal) * 0.5 + 0.5;
                    }
                    if (Diagnostic.w > 0.5)
                    {
                        output.Target2.rgb = Albedo.rgb;
                    }
                    output.Target1 = lerp(output.Target1, MaterialOverride, MaterialMask);
                    if (TextureControl.x > 0.5)
                    {
                        output.Target2.rgb = TestAlbedo.Sample(TestSampler, input.TexCoord).rgb;
                    }
                    return output;
                }

                output.Target0 = float4(normalize(input.WorldNormal) * 0.5 + 0.5, 0.0);
                output.Target1 = 0.0;
                output.Target2 = float4(Albedo.rgb, 0.0);
                output.Target3 = 0.0;
                output.Target4 = 0.0;
                return output;
            }
            """;

        using var compiledWorldVertexShader = ShaderBytecode.Compile(worldShaderSource, "VS", "vs_5_0");
        using var compiledWorldPixelShader = ShaderBytecode.Compile(worldShaderSource, "PS", "ps_5_0");
        worldVertexShader = new VertexShader(device, compiledWorldVertexShader.Bytecode);
        worldPixelShader = new PixelShader(device, compiledWorldPixelShader.Bytecode);
        worldInputLayout = new InputLayout(
            device,
            compiledWorldVertexShader.Bytecode,
            [
                new InputElement("POSITION", 0, SharpDX.DXGI.Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, SharpDX.DXGI.Format.R32G32B32_Float, sizeof(float) * 3, 0),
                new InputElement("TEXCOORD", 0, SharpDX.DXGI.Format.R32G32_Float, sizeof(float) * 6, 0),
            ]
        );
        worldVertexBuffer = new D3D11Buffer(
            device,
            Utilities.SizeOf<WorldVertex>() * 24,
            ResourceUsage.Default,
            BindFlags.VertexBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0
        );
        (materialTestTexture, materialTestTextureView) = CreateMaterialTestTexture();
        materialTestSampler = new SamplerState(
            device,
            new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = Comparison.Never,
                MaximumAnisotropy = 1,
                MipLodBias = 0,
                MinimumLod = 0,
                MaximumLod = 0,
                BorderColor = Color.Transparent,
            }
        );
        worldConstantsBuffer = new D3D11Buffer(
            device,
            Utilities.SizeOf<WorldConstants>(),
            ResourceUsage.Default,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0
        );

        var rasterizerDescription = RasterizerStateDescription.Default();
        rasterizerDescription.CullMode = CullMode.None;
        rasterizerDescription.IsScissorEnabled = true;
        rasterizerState = new RasterizerState(device, rasterizerDescription);
        rasterizerDescription.IsScissorEnabled = false;
        worldRasterizerState = new RasterizerState(device, rasterizerDescription);

        depthOnlyBlendState = CreateWriteMaskBlendState();
        target0RgbBlendState = CreateWriteMaskBlendState(0);
        target2RgbBlendState = CreateWriteMaskBlendState(2);
        worldTriangleBlendState = CreateWriteMaskBlendState(0, 2);
        fullGBufferBlendState = CreateFullWriteBlendState();

        var depthDescription = DepthStencilStateDescription.Default();
        depthDescription.IsDepthEnabled = true;
        depthDescription.DepthWriteMask = DepthWriteMask.All;
        depthDescription.DepthComparison = Comparison.Always;
        depthDescription.IsStencilEnabled = false;
        depthWriteState = new DepthStencilState(device, depthDescription);

        depthDescription.DepthComparison = Comparison.GreaterEqual;
        worldDepthWriteState = new DepthStencilState(device, depthDescription);

        depthDescription.IsStencilEnabled = true;
        depthDescription.StencilReadMask = 0xFF;
        depthDescription.StencilWriteMask = 0xFF;
        depthDescription.FrontFace.Comparison = Comparison.Always;
        depthDescription.FrontFace.FailOperation = StencilOperation.Keep;
        depthDescription.FrontFace.DepthFailOperation = StencilOperation.Keep;
        depthDescription.FrontFace.PassOperation = StencilOperation.Replace;
        depthDescription.BackFace = depthDescription.FrontFace;
        donorStencilWriteState = new DepthStencilState(device, depthDescription);

        depthDescription.IsDepthEnabled = false;
        depthDescription.IsStencilEnabled = false;
        depthDescription.DepthWriteMask = DepthWriteMask.Zero;
        noDepthWriteState = new DepthStencilState(device, depthDescription);

        var vtable = *(nint**)immediateContextPointer;
        omSetRenderTargetsHook = gameInteropProvider.HookFromAddress<OMSetRenderTargetsDelegate>(vtable[33], OMSetRenderTargetsDetour);
        omSetRenderTargetsAndUavsHook = gameInteropProvider.HookFromAddress<OMSetRenderTargetsAndUnorderedAccessViewsDelegate>(
            vtable[34],
            OMSetRenderTargetsAndUavsDetour
        );
        omSetDepthStencilStateHook = gameInteropProvider.HookFromAddress<OMSetDepthStencilStateDelegate>(
            vtable[36],
            OMSetDepthStencilStateDetour
        );
        psSetShaderResourcesHook = gameInteropProvider.HookFromAddress<SetShaderResourcesDelegate>(vtable[8], PSSetShaderResourcesDetour);
        csSetShaderResourcesHook = gameInteropProvider.HookFromAddress<SetShaderResourcesDelegate>(vtable[67], CSSetShaderResourcesDetour);
        drawIndexedHook = gameInteropProvider.HookFromAddress<DrawIndexedDelegate>(vtable[12], DrawIndexedDetour);
        drawHook = gameInteropProvider.HookFromAddress<DrawDelegate>(vtable[13], DrawDetour);
        drawIndexedInstancedHook = gameInteropProvider.HookFromAddress<DrawIndexedInstancedDelegate>(
            vtable[20],
            DrawIndexedInstancedDetour
        );
        drawInstancedHook = gameInteropProvider.HookFromAddress<DrawInstancedDelegate>(vtable[21], DrawInstancedDetour);
        clearRenderTargetViewHook = gameInteropProvider.HookFromAddress<ClearRenderTargetViewDelegate>(
            vtable[50],
            ClearRenderTargetViewDetour
        );
        clearDepthStencilViewHook = gameInteropProvider.HookFromAddress<ClearDepthStencilViewDelegate>(
            vtable[53],
            ClearDepthStencilViewDetour
        );
    }

    private (Texture2D Texture, ShaderResourceView View) CreateMaterialTestTexture()
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
                var bright = (cellX + cellY) % 2 == 0 ? 1f : 0.28f;
                var tint = (x >= size / 2, y >= size / 2) switch
                {
                    (false, false) => new Vector3(1f, 0.18f, 0.18f),
                    (true, false) => new Vector3(0.18f, 1f, 0.18f),
                    (false, true) => new Vector3(0.18f, 0.35f, 1f),
                    _ => new Vector3(1f, 0.82f, 0.18f),
                };
                var grid = x % (size / cells) < 2 || y % (size / cells) < 2;
                var color = grid ? Vector3.One : tint * bright;
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

    private BlendState CreateWriteMaskBlendState(params int[] rgbTargets)
    {
        var description = BlendStateDescription.Default();
        description.IndependentBlendEnable = true;
        for (var index = 0; index < description.RenderTarget.Length; index++)
        {
            description.RenderTarget[index].RenderTargetWriteMask = 0;
        }

        foreach (var rgbTarget in rgbTargets)
        {
            description.RenderTarget[rgbTarget].RenderTargetWriteMask =
                ColorWriteMaskFlags.Red | ColorWriteMaskFlags.Green | ColorWriteMaskFlags.Blue;
        }

        return new BlendState(device, description);
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

    public void Enable()
    {
        omSetRenderTargetsHook.Enable();
        omSetRenderTargetsAndUavsHook.Enable();
        omSetDepthStencilStateHook.Enable();
        psSetShaderResourcesHook.Enable();
        csSetShaderResourcesHook.Enable();
        drawIndexedHook.Enable();
        drawHook.Enable();
        drawIndexedInstancedHook.Enable();
        drawInstancedHook.Enable();
        clearRenderTargetViewHook.Enable();
        clearDepthStencilViewHook.Enable();
    }

    public void Update()
    {
        if (
            lastEnabled == configuration.EnableGBufferProbe
            && lastMode == configuration.GBufferProbeMode
            && lastConfiguredOrdinal == configuration.GBufferProbeCandidateExitOrdinal
        )
        {
            return;
        }

        lock (probeStateLock)
        {
            lastEnabled = configuration.EnableGBufferProbe;
            lastMode = configuration.GBufferProbeMode;
            lastConfiguredOrdinal = configuration.GBufferProbeCandidateExitOrdinal;
            ResetCandidate();
            probeIssuedSinceDepthClear = false;
            nextCandidateOrdinal = 0;
            completedCandidateExits = 0;
            candidateLogCount = 0;
            candidateExitLogCount = 0;
            frameSummaryLogCount = 0;
            injectionCount = 0;
            donorSweepNextIndex = 0;
            donorSweepInjectionLogCount = 0;
            Array.Clear(trackedGBufferResources);
            gBufferConsumerTransitions.Clear();
            gBufferConsumerSummaryLogCount = 0;
            lastCandidateExitDrawSerial = 0;
            worldTriangleVertices = null;
            worldTriangleMarkers = null;
            donorSnapshot?.Dispose();
            donorSnapshot = null;
            donorStencilReference = 0x80;
            log.Information(
                $"G-buffer probe {(lastEnabled ? "enabled" : "disabled")}: mode={lastMode}, candidateExitOrdinal={lastConfiguredOrdinal}."
            );
        }
    }

    public bool TryGetWorldTriangleMarkers(out GBufferWorldTriangleMarker[] markers)
    {
        markers = [];
        if (
            !configuration.EnableGBufferProbe
            || configuration.GBufferProbeMode is not (GBufferProbeMode.WorldTriangle or GBufferProbeMode.DonorOpaqueTuple)
        )
        {
            return false;
        }

        var snapshot = Volatile.Read(ref worldTriangleMarkers);
        if (snapshot is not { Length: > 0 })
        {
            return false;
        }

        markers =
            configuration.GBufferProbeMode == GBufferProbeMode.DonorOpaqueTuple
                ?
                [
                    new GBufferWorldTriangleMarker(
                        "Textured opaque quad",
                        snapshot[6].Center,
                        new System.Numerics.Vector4(1f, 0.1f, 1f, 1f)
                    ),
                ]
                : snapshot.Take(5).ToArray();
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        clearDepthStencilViewHook.Dispose();
        clearRenderTargetViewHook.Dispose();
        drawInstancedHook.Dispose();
        drawIndexedInstancedHook.Dispose();
        drawHook.Dispose();
        drawIndexedHook.Dispose();
        csSetShaderResourcesHook.Dispose();
        psSetShaderResourcesHook.Dispose();
        omSetDepthStencilStateHook.Dispose();
        omSetRenderTargetsAndUavsHook.Dispose();
        omSetRenderTargetsHook.Dispose();

        noDepthWriteState.Dispose();
        donorStencilWriteState.Dispose();
        worldDepthWriteState.Dispose();
        depthWriteState.Dispose();
        donorSnapshot?.Dispose();
        fullGBufferBlendState.Dispose();
        worldTriangleBlendState.Dispose();
        target2RgbBlendState.Dispose();
        target0RgbBlendState.Dispose();
        depthOnlyBlendState.Dispose();
        worldRasterizerState.Dispose();
        rasterizerState.Dispose();
        worldConstantsBuffer.Dispose();
        materialTestSampler.Dispose();
        materialTestTextureView.Dispose();
        materialTestTexture.Dispose();
        worldPixelShader.Dispose();
        worldVertexShader.Dispose();
        worldInputLayout.Dispose();
        worldVertexBuffer.Dispose();
        probeConstantsBuffer.Dispose();
        pixelShader.Dispose();
        vertexShader.Dispose();
        inputLayout.Dispose();
        vertexBuffer.Dispose();
        deferredContext.Dispose();
        immediateContext.Dispose();
    }

    private void OMSetRenderTargetsDetour(nint context, uint numViews, nint* renderTargetViews, nint depthStencilView)
    {
        PendingProbe? pendingProbe = null;
        try
        {
            if (!detouring && context == immediateContextPointer)
            {
                RecordTargetBinding("OMSetRT", numViews, renderTargetViews, depthStencilView);
                pendingProbe = TrackTargetChange(numViews, renderTargetViews, depthStencilView);
            }
        }
        catch (Exception exception)
        {
            log.Warning(exception, "G-buffer probe failed while tracking OMSetRenderTargets.");
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
        if (pendingProbe != null)
        {
            TryIssueProbeSafely(pendingProbe);
        }
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
            if (!detouring && context == immediateContextPointer)
            {
                RecordTargetBinding("OMSetRT+UAV", numRenderTargetViews, renderTargetViews, depthStencilView);
                pendingProbe = TrackTargetChange(numRenderTargetViews, renderTargetViews, depthStencilView);
                TrackUavBinding(unorderedAccessViewStartSlot, numUnorderedAccessViews, unorderedAccessViews);
            }
        }
        catch (Exception exception)
        {
            log.Warning(exception, "G-buffer probe failed while tracking OMSetRenderTargetsAndUnorderedAccessViews.");
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
        if (pendingProbe != null)
        {
            TryIssueProbeSafely(pendingProbe);
        }
    }

    private PendingProbe? TrackTargetChange(uint numViews, nint* renderTargetViews, nint depthStencilView)
    {
        if (!configuration.EnableGBufferProbe)
        {
            ResetCandidate();
            return null;
        }

        if (numViews == uint.MaxValue)
        {
            return null;
        }

        var match = MatchGBuffers(numViews, renderTargetViews);
        var isCandidate = depthStencilView != 0 && match.MatchedCount >= MinimumMatchedGBuffers;
        PendingProbe? pendingProbe = null;

        if (candidateActive && !isCandidate)
        {
            pendingProbe = FinishCandidate(DescribeTargets(numViews, renderTargetViews, depthStencilView));
            ResetCandidate();
        }

        if (!isCandidate)
        {
            return pendingProbe;
        }

        if (!candidateActive)
        {
            candidateActive = true;
            candidateOrdinal = nextCandidateOrdinal++;
            candidateStartTimestamp = Stopwatch.GetTimestamp();
            candidateViewport = CaptureViewport();
            candidateDepthStencilState = lastDepthStencilState;
            candidateDepthStencilTransitions.Clear();
            candidateDepthStencilTransitions.Add($"draw=0(initial):{lastDepthStencilState}");
            candidateUavTransitions.Clear();
            candidateDrawCount = 0;
            candidateIndexedDrawCount = 0;
            donorSweepNextIndex = 0;
        }

        candidateDepthStencil = depthStencilView;
        candidateWidth = match.Width;
        candidateHeight = match.Height;
        candidateRenderTargets = new nint[numViews];
        for (var index = 0; index < numViews; index++)
        {
            candidateRenderTargets[index] = renderTargetViews[index];
        }
        TrackGBufferResources(numViews, renderTargetViews);

        if (candidateLogCount++ < 1)
        {
            log.Information(
                $"G-buffer candidate begin: ordinal={candidateOrdinal}, matched={match.MatchedCount}, views={numViews}, size={match.Width}x{match.Height}, viewport={candidateViewport}, dsv=0x{depthStencilView:X}."
            );
            log.Information($"G-buffer pre-candidate OM history: {string.Join(" || ", targetBindingHistory)}.");
        }

        return pendingProbe;
    }

    private void RecordTargetBinding(string source, uint numViews, nint* renderTargetViews, nint depthStencilView)
    {
        var targets = new List<string>();
        if (numViews != uint.MaxValue && renderTargetViews != null)
        {
            for (var index = 0; index < Math.Min(numViews, 8); index++)
            {
                targets.Add($"0x{renderTargetViews[index]:X}");
            }
        }

        var count = numViews == uint.MaxValue ? "KEEP" : numViews.ToString();
        targetBindingHistory.Enqueue(
            $"serial={immediateDrawSerial},previousDraws={drawsSinceTargetBinding},{source}:rtvCount={count},"
                + $"rtvs=[{string.Join(",", targets)}],dsv=0x{depthStencilView:X}"
        );
        while (targetBindingHistory.Count > 32)
        {
            targetBindingHistory.Dequeue();
        }

        drawsSinceTargetBinding = 0;
    }

    private void TrackGBufferResources(uint numViews, nint* renderTargetViews)
    {
        if (renderTargetViews == null)
        {
            return;
        }

        var count = Math.Min(Math.Min((int)numViews, trackedGBufferResources.Length), candidateRenderTargets.Length);
        for (var index = 0; index < count; index++)
        {
            if (renderTargetViews[index] == 0)
            {
                continue;
            }

            var resource = GetViewResource(renderTargetViews[index]);
            if (resource == 0)
            {
                continue;
            }

            trackedGBufferResources[index] = resource;
            Marshal.Release(resource);
        }
    }

    private void PSSetShaderResourcesDetour(nint context, uint startSlot, uint numViews, nint* shaderResourceViews)
    {
        try
        {
            TrackGBufferConsumers("PS", context, startSlot, numViews, shaderResourceViews);
        }
        catch (Exception exception)
        {
            log.Warning(exception, "G-buffer probe failed while tracking PS shader resources.");
        }
        finally
        {
            psSetShaderResourcesHook.Original(context, startSlot, numViews, shaderResourceViews);
        }
    }

    private void CSSetShaderResourcesDetour(nint context, uint startSlot, uint numViews, nint* shaderResourceViews)
    {
        try
        {
            TrackGBufferConsumers("CS", context, startSlot, numViews, shaderResourceViews);
        }
        catch (Exception exception)
        {
            log.Warning(exception, "G-buffer probe failed while tracking CS shader resources.");
        }
        finally
        {
            csSetShaderResourcesHook.Original(context, startSlot, numViews, shaderResourceViews);
        }
    }

    private void TrackGBufferConsumers(string stage, nint context, uint startSlot, uint numViews, nint* shaderResourceViews)
    {
        if (
            detouring
            || context != immediateContextPointer
            || !configuration.EnableGBufferProbe
            || shaderResourceViews == null
            || numViews == 0
            || gBufferConsumerSummaryLogCount > 0
            || gBufferConsumerTransitions.Count >= 64
            || trackedGBufferResources.All(resource => resource == 0)
        )
        {
            return;
        }

        var matches = new List<string>();
        for (var viewIndex = 0; viewIndex < numViews; viewIndex++)
        {
            var view = shaderResourceViews[viewIndex];
            if (view == 0)
            {
                continue;
            }

            var resource = GetViewResource(view);
            if (resource == 0)
            {
                continue;
            }

            for (var gBufferIndex = 0; gBufferIndex < trackedGBufferResources.Length; gBufferIndex++)
            {
                if (resource == trackedGBufferResources[gBufferIndex])
                {
                    matches.Add($"t{startSlot + viewIndex}->G{gBufferIndex}");
                }
            }

            Marshal.Release(resource);
        }

        if (matches.Count == 0)
        {
            return;
        }

        var phase = candidateActive
            ? $"candidate={candidateOrdinal},candidateDraw={candidateDrawCount}"
            : $"outsideCandidate,drawsAfterExit={immediateDrawSerial - lastCandidateExitDrawSerial}";
        var transition = $"serial={immediateDrawSerial},{phase},{stage}:[{string.Join(",", matches)}]";
        if (gBufferConsumerTransitions.Count == 0 || gBufferConsumerTransitions[^1] != transition)
        {
            gBufferConsumerTransitions.Add(transition);
        }
    }

    private static nint GetViewResource(nint view)
    {
        if (view == 0)
        {
            return 0;
        }

        nint resource = 0;
        var vtable = *(nint**)view;
        var getResource = (delegate* unmanaged[Stdcall]<nint, nint*, void>)vtable[7];
        getResource(view, &resource);
        return resource;
    }

    private void TrackUavBinding(uint startSlot, uint count, nint* unorderedAccessViews)
    {
        if (!candidateActive || candidateUavTransitions.Count >= 32)
        {
            return;
        }

        string binding;
        if (count == uint.MaxValue)
        {
            binding = $"draw={candidateDrawCount}:start={startSlot},count=KEEP";
        }
        else if (count == 0)
        {
            binding = $"draw={candidateDrawCount}:start={startSlot},count=0";
        }
        else
        {
            var pointers = new List<string>((int)Math.Min(count, 16));
            if (unorderedAccessViews != null)
            {
                for (var index = 0; index < Math.Min(count, 16); index++)
                {
                    pointers.Add($"0x{unorderedAccessViews[index]:X}");
                }
            }

            binding = $"draw={candidateDrawCount}:start={startSlot},count={count},uavs=[{string.Join(",", pointers)}]";
        }

        if (candidateUavTransitions.Count == 0 || candidateUavTransitions[^1] != binding)
        {
            candidateUavTransitions.Add(binding);
        }
    }

    private static (int MatchedCount, uint Width, uint Height) MatchGBuffers(uint numViews, nint* renderTargetViews)
    {
        var manager = RenderTargetManager.Instance();
        if (manager == null || renderTargetViews == null)
        {
            return default;
        }

        var matchedCount = 0;
        uint width = 0;
        uint height = 0;
        var count = Math.Min((int)numViews, manager->GBuffers.Length);
        for (var index = 0; index < count; index++)
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

    private PendingProbe? FinishCandidate(string nextTargets)
    {
        var endTimestamp = Stopwatch.GetTimestamp();
        completedCandidateExits++;
        var startMilliseconds = GetRelativeMilliseconds(candidateStartTimestamp);
        var endMilliseconds = GetRelativeMilliseconds(endTimestamp);
        var selected = candidateOrdinal == configuration.GBufferProbeCandidateExitOrdinal;
        lastCandidateExitDrawSerial = immediateDrawSerial;

        if (candidateExitLogCount++ < DetailedExitLogLimit)
        {
            log.Information(
                $"G-buffer candidate exit: ordinal={candidateOrdinal}, draws={candidateDrawCount}, indexedDraws={candidateIndexedDrawCount}, "
                    + $"rtvs=[{string.Join(",", candidateRenderTargets.Select(pointer => $"0x{pointer:X}"))}], dsv=0x{candidateDepthStencil:X}, "
                    + $"viewport={candidateViewport}, startMs={startMilliseconds:F3}, endMs={endMilliseconds:F3}, "
                    + $"durationMs={endMilliseconds - startMilliseconds:F3}, nativeOM={candidateDepthStencilState}, "
                    + $"next={nextTargets}, selected={selected}."
            );
            log.Information(
                $"G-buffer OM transitions: ordinal={candidateOrdinal}, {string.Join(" || ", candidateDepthStencilTransitions)}."
            );
            log.Information(
                $"G-buffer UAV transitions: ordinal={candidateOrdinal}, "
                    + (candidateUavTransitions.Count == 0 ? "none" : string.Join(" || ", candidateUavTransitions))
                    + "."
            );
        }

        if (!selected)
        {
            return null;
        }

        return new PendingProbe(
            [.. candidateRenderTargets],
            candidateDepthStencil,
            candidateWidth,
            candidateHeight,
            candidateOrdinal,
            candidateDrawCount,
            candidateIndexedDrawCount,
            startMilliseconds,
            endMilliseconds,
            candidateViewport,
            nextTargets,
            candidateDepthStencilState,
            string.Join(" || ", candidateDepthStencilTransitions)
        );
    }

    private double GetRelativeMilliseconds(long timestamp) => (timestamp - controllerStartTimestamp) * 1000d / Stopwatch.Frequency;

    private string CaptureViewport()
    {
        try
        {
            var viewports = immediateContext.Rasterizer.GetViewports<Viewport>();
            return viewports.Length == 0
                ? "none"
                : string.Join(
                    ",",
                    viewports.Select(viewport => $"{viewport.X:F0},{viewport.Y:F0},{viewport.Width:F0}x{viewport.Height:F0}")
                );
        }
        catch (Exception exception)
        {
            return $"error:{exception.GetType().Name}";
        }
    }

    private static string DescribeTargets(uint numViews, nint* renderTargetViews, nint depthStencilView)
    {
        if (renderTargetViews == null || numViews == 0)
        {
            return $"rtvs=[],dsv=0x{depthStencilView:X}";
        }

        var count = Math.Min((int)numViews, 8);
        var pointers = new string[count];
        for (var index = 0; index < count; index++)
        {
            pointers[index] = $"0x{renderTargetViews[index]:X}";
        }

        return $"rtvs=[{string.Join(",", pointers)}],dsv=0x{depthStencilView:X}";
    }

    private void TryIssueProbeSafely(PendingProbe probe)
    {
        try
        {
            TryIssueProbe(probe);
        }
        catch (Exception exception)
        {
            log.Warning(exception, $"G-buffer probe failed after Original for ordinal {probe.Ordinal}.");
        }
        finally
        {
            probe.Dispose();
        }
    }

    private void TryIssueProbe(PendingProbe probe, bool earlyDonorInjection = false)
    {
        lock (probeStateLock)
        {
            TryIssueProbeLocked(probe, earlyDonorInjection);
        }
    }

    private void TryIssueProbeLocked(PendingProbe probe, bool earlyDonorInjection)
    {
        if (probeIssuedSinceDepthClear)
        {
            return;
        }

        if (configuration.GBufferProbeMode == GBufferProbeMode.NoOp)
        {
            probeIssuedSinceDepthClear = true;
            injectionCount++;
            LogProbeResult(probe, "NoOp selected; draw skipped");
            return;
        }

        if (probe.DrawCount == 0 || probe.RenderTargets.Length == 0 || probe.Width == 0 || probe.Height == 0)
        {
            if (!earlyDonorInjection)
            {
                LogProbeResult(probe, "draw skipped because the candidate was incomplete");
                return;
            }

            if (probe.RenderTargets.Length == 0 || probe.Width == 0 || probe.Height == 0)
            {
                LogProbeResult(probe, "early donor draw skipped because the candidate was incomplete");
                return;
            }
        }

        var donorCaptureNeeded = configuration.GBufferProbeMode == GBufferProbeMode.DonorOpaqueTuple && donorSnapshot == null;
        try
        {
            var (blendState, depthState) = configuration.GBufferProbeMode switch
            {
                GBufferProbeMode.DepthOnly => (depthOnlyBlendState, depthWriteState),
                GBufferProbeMode.Target0Rgb => (target0RgbBlendState, noDepthWriteState),
                GBufferProbeMode.Target2Rgb => (target2RgbBlendState, noDepthWriteState),
                GBufferProbeMode.WorldTriangle => (worldTriangleBlendState, worldDepthWriteState),
                GBufferProbeMode.DonorOpaqueTuple => (fullGBufferBlendState, worldDepthWriteState),
                _ => throw new InvalidOperationException($"Unsupported G-buffer probe mode: {configuration.GBufferProbeMode}"),
            };

            var isWorldTriangle = configuration.GBufferProbeMode is GBufferProbeMode.WorldTriangle or GBufferProbeMode.DonorOpaqueTuple;
            var isDonorTuple = configuration.GBufferProbeMode == GBufferProbeMode.DonorOpaqueTuple;
            Matrix controlViewProjection = default;
            Matrix sceneViewProjection = default;
            if (
                isWorldTriangle
                && !TryUpdateWorldTriangleResources(probe.Width, probe.Height, out controlViewProjection, out sceneViewProjection)
            )
            {
                LogProbeResult(probe, "world triangle skipped because the active scene camera was unavailable");
                return;
            }

            detouring = true;
            deferredContext.ClearState();
            if (isDonorTuple && !TryCaptureDonorSnapshot(probe))
            {
                LogProbeResult(probe, "donor tuple skipped because snapshot capture failed");
                return;
            }

            if (donorCaptureNeeded)
            {
                using var captureCommandList = deferredContext.FinishCommandList(false);
                immediateContext.ExecuteCommandList(captureCommandList, true);
                LogDonorReadbackOnce();
                probeIssuedSinceDepthClear = true;
                injectionCount++;
                LogProbeResult(probe, "donor captured; world draw deferred until next frame's first candidate draw");
                return;
            }

            deferredContext.OutputMerger.SetTargets(probe.DepthStencil, [.. probe.RenderTargets.Select(target => target!)]);
            deferredContext.OutputMerger.SetBlendState(blendState);
            deferredContext.OutputMerger.SetDepthStencilState(depthState);
            deferredContext.Rasterizer.SetViewport(0, 0, probe.Width, probe.Height, 0, 1);
            deferredContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            deferredContext.HullShader.Set(null);
            deferredContext.DomainShader.Set(null);
            deferredContext.GeometryShader.Set(null);

            if (isWorldTriangle)
            {
                deferredContext.Rasterizer.State = worldRasterizerState;
                deferredContext.InputAssembler.InputLayout = worldInputLayout;
                deferredContext.InputAssembler.SetVertexBuffers(
                    0,
                    new VertexBufferBinding(worldVertexBuffer, Utilities.SizeOf<WorldVertex>(), 0)
                );
                deferredContext.VertexShader.Set(worldVertexShader);
                deferredContext.VertexShader.SetConstantBuffer(0, worldConstantsBuffer);
                deferredContext.PixelShader.Set(worldPixelShader);
                deferredContext.PixelShader.SetConstantBuffer(0, worldConstantsBuffer);
                if (isDonorTuple)
                {
                    DrawTexturedDonorQuad(controlViewProjection);
                }
                else
                {
                    DrawWorldTriangleDiagnostics(controlViewProjection);
                }
            }
            else
            {
                deferredContext.Rasterizer.State = rasterizerState;
                var left = Math.Max(0, ((int)probe.Width - ProbeSize) / 2);
                var top = Math.Max(0, ((int)probe.Height - ProbeSize) / 2);
                deferredContext.Rasterizer.SetScissorRectangle(left, top, left + ProbeSize, top + ProbeSize);
                deferredContext.InputAssembler.InputLayout = inputLayout;
                deferredContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vertexBuffer, sizeof(float) * 2, 0));
                deferredContext.VertexShader.Set(vertexShader);
                deferredContext.PixelShader.Set(pixelShader);
                var constants = new ProbeConstants { SplitX = probe.Width / 2f };
                deferredContext.UpdateSubresource(ref constants, probeConstantsBuffer);
                deferredContext.PixelShader.SetConstantBuffer(0, probeConstantsBuffer);
                deferredContext.Draw(3, 0);
            }

            using var commandList = deferredContext.FinishCommandList(false);
            immediateContext.ExecuteCommandList(commandList, true);
            if (isDonorTuple)
            {
                LogDonorReadbackOnce();
            }

            probeIssuedSinceDepthClear = true;
            injectionCount++;
            var geometry = isWorldTriangle
                ? earlyDonorInjection
                    ? "world triangle at candidate begin"
                    : "world triangle"
                : $"{ProbeSize}x{ProbeSize}";
            LogProbeResult(probe, $"draw #{injectionCount} issued, geometry={geometry}, targets={probe.RenderTargets.Length}");
        }
        finally
        {
            detouring = false;
            deferredContext.ClearState();
        }
    }

    private bool TryCaptureDonorSnapshot(PendingProbe probe)
    {
        if (donorSnapshot != null)
        {
            return true;
        }

        if (probe.RenderTargets.Length < 5 || probe.RenderTargets.Take(5).Any(target => target == null))
        {
            return false;
        }

        var textures = new List<Texture2D>(5);
        var views = new List<ShaderResourceView>(5);
        var stagingTextures = new List<Texture2D>(5);
        var bytesPerPixel = new List<int>(5);
        var formats = new List<string>(5);
        Texture2D? depthStencilStagingTexture = null;
        try
        {
            var sourceX = Math.Clamp((int)(probe.Width * DonorSampleNormalized.X), 0, (int)probe.Width - 1);
            var sourceY = Math.Clamp((int)(probe.Height * DonorSampleNormalized.Y), 0, (int)probe.Height - 1);
            var sourceRegion = new ResourceRegion(sourceX, sourceY, 0, sourceX + 1, sourceY + 1, 1);
            for (var index = 0; index < 5; index++)
            {
                var renderTarget = probe.RenderTargets[index]!;
                using var source = renderTarget.ResourceAs<Texture2D>();
                var sourceDescription = source.Description;
                var viewDescription = renderTarget.Description;
                if (sourceDescription.SampleDescription.Count != 1)
                {
                    throw new NotSupportedException($"G{index} is MSAA x{sourceDescription.SampleDescription.Count}.");
                }

                var donorDescription = sourceDescription;
                donorDescription.Width = 1;
                donorDescription.Height = 1;
                donorDescription.MipLevels = 1;
                donorDescription.ArraySize = 1;
                donorDescription.Usage = ResourceUsage.Default;
                donorDescription.BindFlags = BindFlags.ShaderResource;
                donorDescription.CpuAccessFlags = CpuAccessFlags.None;
                donorDescription.OptionFlags = ResourceOptionFlags.None;

                var texture = new Texture2D(device, donorDescription);
                textures.Add(texture);
                var srvDescription = new ShaderResourceViewDescription
                {
                    Format = viewDescription.Format,
                    Dimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = { MostDetailedMip = 0, MipLevels = 1 },
                };
                var view = new ShaderResourceView(device, texture, srvDescription);
                views.Add(view);

                var stagingDescription = donorDescription;
                stagingDescription.Usage = ResourceUsage.Staging;
                stagingDescription.BindFlags = BindFlags.None;
                stagingDescription.CpuAccessFlags = CpuAccessFlags.Read;
                var stagingTexture = new Texture2D(device, stagingDescription);
                stagingTextures.Add(stagingTexture);
                bytesPerPixel.Add(GetFormatBytesPerPixel(viewDescription.Format));
                formats.Add($"G{index}:resource={sourceDescription.Format},rtv={viewDescription.Format}");
                deferredContext.CopySubresourceRegion(source, 0, sourceRegion, texture, 0, 0, 0, 0);
                deferredContext.CopyResource(texture, stagingTexture);
            }

            using var depthStencilSource = probe.DepthStencil.ResourceAs<Texture2D>();
            var depthStencilDescription = depthStencilSource.Description;
            var depthStencilStagingDescription = depthStencilDescription;
            depthStencilStagingDescription.Usage = ResourceUsage.Staging;
            depthStencilStagingDescription.BindFlags = BindFlags.None;
            depthStencilStagingDescription.CpuAccessFlags = CpuAccessFlags.Read;
            depthStencilStagingDescription.OptionFlags = ResourceOptionFlags.None;
            depthStencilStagingTexture = new Texture2D(device, depthStencilStagingDescription);
            deferredContext.CopyResource(depthStencilSource, depthStencilStagingTexture);
            var depthStencilBytesPerPixel = GetDepthStencilFormatBytesPerPixel(depthStencilDescription.Format);

            donorSnapshot = new DonorSnapshot(
                [.. textures],
                [.. views],
                [.. stagingTextures],
                [.. bytesPerPixel],
                depthStencilStagingTexture,
                depthStencilBytesPerPixel,
                sourceX,
                sourceY,
                depthStencilDescription.Format.ToString(),
                string.Join(";", formats)
            );
            log.Information($"G-buffer donor tuple captured once at ({probe.Width / 2},{probe.Height / 2}): {donorSnapshot.Formats}.");
            return true;
        }
        catch (Exception exception)
        {
            foreach (var view in views)
            {
                view.Dispose();
            }

            foreach (var texture in textures)
            {
                texture.Dispose();
            }

            foreach (var texture in stagingTextures)
            {
                texture.Dispose();
            }

            depthStencilStagingTexture?.Dispose();

            log.Error(exception, "Failed to capture G-buffer donor tuple.");
            return false;
        }
    }

    private void LogDonorReadbackOnce()
    {
        if (donorSnapshot is not { ReadbackPending: true } snapshot)
        {
            return;
        }

        var values = new List<string>(snapshot.StagingTextures.Length);
        try
        {
            for (var index = 0; index < snapshot.StagingTextures.Length; index++)
            {
                var data = immediateContext.MapSubresource(snapshot.StagingTextures[index], 0, MapMode.Read, MapFlags.None);
                try
                {
                    var bytes = new byte[snapshot.BytesPerPixel[index]];
                    Marshal.Copy(data.DataPointer, bytes, 0, bytes.Length);
                    values.Add($"G{index}={Convert.ToHexString(bytes)}");
                }
                finally
                {
                    immediateContext.UnmapSubresource(snapshot.StagingTextures[index], 0);
                }
            }

            var depthStencilData = immediateContext.MapSubresource(snapshot.DepthStencilStagingTexture, 0, MapMode.Read, MapFlags.None);
            try
            {
                var depthStencilBytes = new byte[snapshot.DepthStencilBytesPerPixel];
                var pixelAddress =
                    depthStencilData.DataPointer
                    + snapshot.SampleY * depthStencilData.RowPitch
                    + snapshot.SampleX * snapshot.DepthStencilBytesPerPixel;
                Marshal.Copy(pixelAddress, depthStencilBytes, 0, depthStencilBytes.Length);
                donorStencilReference = GetStencilByte(depthStencilBytes);
                values.Add(
                    $"DS[{snapshot.DepthStencilFormat}]={Convert.ToHexString(depthStencilBytes)},stencil=0x{donorStencilReference:X2}"
                );
            }
            finally
            {
                immediateContext.UnmapSubresource(snapshot.DepthStencilStagingTexture, 0);
            }

            snapshot.ReadbackPending = false;
            log.Information($"G-buffer donor raw readback: {string.Join(",", values)}.");
        }
        catch (Exception exception)
        {
            snapshot.ReadbackPending = false;
            log.Error(exception, "Failed to read back G-buffer donor tuple.");
        }
    }

    private static int GetFormatBytesPerPixel(SharpDX.DXGI.Format format) =>
        format switch
        {
            SharpDX.DXGI.Format.B8G8R8A8_UNorm => 4,
            SharpDX.DXGI.Format.R16G16B16A16_Float => 8,
            _ => 16,
        };

    private static int GetDepthStencilFormatBytesPerPixel(SharpDX.DXGI.Format format) =>
        format switch
        {
            SharpDX.DXGI.Format.R24G8_Typeless or SharpDX.DXGI.Format.D24_UNorm_S8_UInt => 4,
            SharpDX.DXGI.Format.R32G8X24_Typeless or SharpDX.DXGI.Format.D32_Float_S8X24_UInt => 8,
            _ => throw new NotSupportedException($"Unsupported depth-stencil format for donor readback: {format}."),
        };

    private static byte GetStencilByte(byte[] depthStencilBytes) =>
        depthStencilBytes.Length switch
        {
            4 => depthStencilBytes[3],
            8 => depthStencilBytes[4],
            _ => 0,
        };

    private void LogProbeResult(PendingProbe probe, string result)
    {
        if (injectionCount <= 2 || injectionCount % 1800 == 0)
        {
            log.Information(
                $"G-buffer probe: ordinal={probe.Ordinal}, mode={configuration.GBufferProbeMode}, draws={probe.DrawCount}, "
                    + $"indexedDraws={probe.IndexedDrawCount}, viewport={probe.Viewport}, nativeOM={probe.NativeDepthStencilState}, "
                    + $"next={probe.NextTargets}, result={result}."
            );
        }
    }

    private bool TryUpdateWorldTriangleResources(
        uint viewportWidth,
        uint viewportHeight,
        out Matrix controlViewProjection,
        out Matrix sceneViewProjection
    )
    {
        controlViewProjection = default;
        sceneViewProjection = default;
        var control = Control.Instance();
        var activeCamera = control != null ? control->CameraManager.GetActiveCamera() : null;
        var renderCamera = activeCamera != null ? activeCamera->SceneCamera.RenderCamera : null;
        if (activeCamera == null || renderCamera == null)
        {
            return false;
        }

        var viewMatrix = *(Matrix*)&activeCamera->SceneCamera.ViewMatrix;
        var projectionMatrix = *(Matrix*)&renderCamera->ProjectionMatrix;
        controlViewProjection = *(Matrix*)&control->ViewProjectionMatrix;
        sceneViewProjection = viewMatrix * projectionMatrix;

        if (worldTriangleVertices == null)
        {
            var cameraPosition = new Vector3(
                activeCamera->SceneCamera.Position.X,
                activeCamera->SceneCamera.Position.Y,
                activeCamera->SceneCamera.Position.Z
            );
            var definitions = new (string Label, float ScreenX, float ScreenY, Vector4 Color)[]
            {
                ("G2 R", 0.30f, 0.32f, new Vector4(1f, 0.1f, 0.1f, 1f)),
                ("G2 G", 0.50f, 0.32f, new Vector4(0.1f, 1f, 0.1f, 1f)),
                ("G2 B", 0.70f, 0.32f, new Vector4(0.1f, 0.3f, 1f, 1f)),
                ("G2 White", 0.30f, 0.62f, new Vector4(1f, 1f, 1f, 1f)),
                ("G2 Black / donor preserve", 0.50f, 0.62f, new Vector4(0.25f, 0.25f, 0.25f, 1f)),
                ("Donor write stencil 0x80", 0.70f, 0.62f, new Vector4(1f, 0.1f, 1f, 1f)),
            };

            var vertices = new List<WorldVertex>(definitions.Length * 3 + 6);
            var markers = new List<GBufferWorldTriangleMarker>(definitions.Length + 1);
            foreach (var definition in definitions)
            {
                var center = GetScreenRayPoint(activeCamera, viewportWidth, viewportHeight, definition.ScreenX, definition.ScreenY);
                var p0 = GetScreenRayPoint(
                    activeCamera,
                    viewportWidth,
                    viewportHeight,
                    definition.ScreenX - 0.045f,
                    definition.ScreenY + 0.065f
                );
                var p1 = GetScreenRayPoint(
                    activeCamera,
                    viewportWidth,
                    viewportHeight,
                    definition.ScreenX + 0.045f,
                    definition.ScreenY + 0.065f
                );
                var p2 = GetScreenRayPoint(activeCamera, viewportWidth, viewportHeight, definition.ScreenX, definition.ScreenY - 0.065f);
                var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
                if (Vector3.Dot(normal, cameraPosition - center) < 0)
                {
                    normal = -normal;
                }

                vertices.Add(
                    new WorldVertex
                    {
                        Position = p0,
                        Normal = normal,
                        TexCoord = new Vector2(0f, 1f),
                    }
                );
                vertices.Add(
                    new WorldVertex
                    {
                        Position = p1,
                        Normal = normal,
                        TexCoord = new Vector2(1f, 1f),
                    }
                );
                vertices.Add(
                    new WorldVertex
                    {
                        Position = p2,
                        Normal = normal,
                        TexCoord = new Vector2(0.5f, 0f),
                    }
                );
                markers.Add(
                    new GBufferWorldTriangleMarker(
                        definition.Label,
                        new System.Numerics.Vector3(center.X, center.Y, center.Z),
                        new System.Numerics.Vector4(definition.Color.X, definition.Color.Y, definition.Color.Z, definition.Color.W)
                    )
                );
            }

            var quadCenter = GetScreenRayPoint(activeCamera, viewportWidth, viewportHeight, 0.50f, 0.48f);
            var forward = Vector3.Normalize(quadCenter - cameraPosition);
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
            var bottomLeft = quadCenter - horizontal - vertical;
            var bottomRight = quadCenter + horizontal - vertical;
            var topLeft = quadCenter - horizontal + vertical;
            var topRight = quadCenter + horizontal + vertical;
            var quadNormal = Vector3.Normalize(Vector3.Cross(bottomRight - bottomLeft, topLeft - bottomLeft));
            if (Vector3.Dot(quadNormal, cameraPosition - quadCenter) < 0)
            {
                quadNormal = -quadNormal;
            }

            vertices.Add(
                new WorldVertex
                {
                    Position = bottomLeft,
                    Normal = quadNormal,
                    TexCoord = new Vector2(0f, 1f),
                }
            );
            vertices.Add(
                new WorldVertex
                {
                    Position = bottomRight,
                    Normal = quadNormal,
                    TexCoord = new Vector2(1f, 1f),
                }
            );
            vertices.Add(
                new WorldVertex
                {
                    Position = topLeft,
                    Normal = quadNormal,
                    TexCoord = new Vector2(0f, 0f),
                }
            );
            vertices.Add(
                new WorldVertex
                {
                    Position = topLeft,
                    Normal = quadNormal,
                    TexCoord = new Vector2(0f, 0f),
                }
            );
            vertices.Add(
                new WorldVertex
                {
                    Position = bottomRight,
                    Normal = quadNormal,
                    TexCoord = new Vector2(1f, 1f),
                }
            );
            vertices.Add(
                new WorldVertex
                {
                    Position = topRight,
                    Normal = quadNormal,
                    TexCoord = new Vector2(1f, 0f),
                }
            );
            markers.Add(
                new GBufferWorldTriangleMarker(
                    "Textured opaque quad",
                    new System.Numerics.Vector3(quadCenter.X, quadCenter.Y, quadCenter.Z),
                    new System.Numerics.Vector4(1f, 0.1f, 1f, 1f)
                )
            );

            worldTriangleVertices = [.. vertices];
            worldTriangleMarkers = [.. markers];

            log.Information(
                "World-triangle diagnostics initialized: matrixSources=Control.ViewProjectionMatrix|Scene.Camera.ViewMatrix*Render.Camera.ProjectionMatrix, "
                    + $"camera={FormatVector(cameraPosition)}, matrixMaxDelta={GetMatrixMaxDelta(controlViewProjection, sceneViewProjection):G4}, "
                    + "placement=Scene.Camera.ScreenPointToRay, variants=G2R,G2G,G2B,G2White,G2Black,DonorTuple, cull=None."
            );
        }

        deferredContext.UpdateSubresource(worldTriangleVertices, worldVertexBuffer);
        return true;
    }

    private void DrawWorldTriangleDiagnostics(Matrix controlViewProjection)
    {
        var colors = new[]
        {
            new Vector4(1f, 0f, 0f, 1f),
            new Vector4(0f, 1f, 0f, 1f),
            new Vector4(0f, 0f, 1f, 1f),
            new Vector4(1f, 1f, 1f, 1f),
            new Vector4(0f, 0f, 0f, 1f),
        };

        controlViewProjection.Transpose();
        deferredContext.OutputMerger.SetDepthStencilState(noDepthWriteState);
        deferredContext.OutputMerger.SetBlendState(target2RgbBlendState);
        for (var index = 0; index < colors.Length; index++)
        {
            var constants = new WorldConstants
            {
                ViewProjection = controlViewProjection,
                Albedo = colors[index],
                Diagnostic = Vector4.Zero,
            };
            deferredContext.UpdateSubresource(ref constants, worldConstantsBuffer);
            deferredContext.Draw(3, index * 3);
        }
    }

    private void DrawTexturedDonorQuad(Matrix controlViewProjection)
    {
        if (donorSnapshot == null)
        {
            return;
        }

        controlViewProjection.Transpose();
        var constants = new WorldConstants
        {
            ViewProjection = controlViewProjection,
            Diagnostic = new Vector4(0f, 1f, 0f, 0f),
            TextureControl = new Vector4(1f, 0f, 0f, 0f),
        };
        deferredContext.OutputMerger.SetBlendState(fullGBufferBlendState);
        deferredContext.PixelShader.SetShaderResources(0, donorSnapshot.Views);
        deferredContext.PixelShader.SetShaderResource(5, materialTestTextureView);
        deferredContext.PixelShader.SetSampler(0, materialTestSampler);
        deferredContext.UpdateSubresource(ref constants, worldConstantsBuffer);
        deferredContext.OutputMerger.SetDepthStencilState(donorStencilWriteState, donorStencilReference);
        deferredContext.Draw(6, 18);
    }

    private void DrawDonorOpaqueTuple(
        Matrix controlViewProjection,
        int triangleIndex,
        Vector3? albedoOverride = null,
        Vector4 materialOverride = default,
        Vector4 materialMask = default
    )
    {
        if (donorSnapshot == null)
        {
            return;
        }

        controlViewProjection.Transpose();
        var constants = new WorldConstants
        {
            ViewProjection = controlViewProjection,
            Albedo = albedoOverride.HasValue ? new Vector4(albedoOverride.Value, 1f) : Vector4.Zero,
            Diagnostic = new Vector4(0f, 1f, 0f, albedoOverride.HasValue ? 1f : 0f),
            MaterialOverride = materialOverride,
            MaterialMask = materialMask,
        };
        deferredContext.OutputMerger.SetBlendState(fullGBufferBlendState);
        deferredContext.PixelShader.SetShaderResources(0, donorSnapshot.Views);
        deferredContext.UpdateSubresource(ref constants, worldConstantsBuffer);
        deferredContext.OutputMerger.SetDepthStencilState(donorStencilWriteState, donorStencilReference);
        deferredContext.Draw(3, triangleIndex * 3);
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

    private static float GetMatrixMaxDelta(Matrix left, Matrix right)
    {
        var maxDelta = 0f;
        for (var index = 0; index < 16; index++)
        {
            maxDelta = Math.Max(maxDelta, Math.Abs(left[index] - right[index]));
        }

        return maxDelta;
    }

    private static string FormatVector(Vector3 value) => $"({value.X:F3},{value.Y:F3},{value.Z:F3})";

    private void TryIssueScheduledDonorProbe()
    {
        if (
            detouring
            || !candidateActive
            || donorSnapshot == null
            || !configuration.EnableGBufferProbe
            || configuration.GBufferProbeMode != GBufferProbeMode.DonorOpaqueTuple
            || candidateOrdinal != configuration.GBufferProbeCandidateExitOrdinal
            || candidateDepthStencil == 0
            || donorSweepNextIndex >= DonorSweepDrawOrdinals.Length
            || candidateDrawCount < DonorSweepDrawOrdinals[donorSweepNextIndex]
        )
        {
            return;
        }

        try
        {
            var now = Stopwatch.GetTimestamp();
            using var probe = new PendingProbe(
                [.. candidateRenderTargets],
                candidateDepthStencil,
                candidateWidth,
                candidateHeight,
                candidateOrdinal,
                candidateDrawCount,
                candidateIndexedDrawCount,
                GetRelativeMilliseconds(candidateStartTimestamp),
                GetRelativeMilliseconds(now),
                candidateViewport,
                $"candidate-active-before-draw-{candidateDrawCount}",
                candidateDepthStencilState,
                string.Join(" || ", candidateDepthStencilTransitions)
            );
            var triangleIndex = donorSweepNextIndex++;
            IssueDonorTriangle(probe, triangleIndex);
            donorSweepInjectionLogCount++;
            if (donorSweepInjectionLogCount <= DonorSweepDrawOrdinals.Length)
            {
                log.Information(
                    $"G-buffer donor sweep injected triangle {triangleIndex} before candidate draw {candidateDrawCount}, stencil=0x{donorStencilReference:X2}."
                );
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, $"Failed to inject donor tuple before candidate draw {candidateDrawCount}.");
        }
    }

    private void IssueDonorTriangle(PendingProbe probe, int triangleIndex)
    {
        if (donorSnapshot == null || !TryUpdateWorldTriangleResources(probe.Width, probe.Height, out var controlViewProjection, out _))
        {
            return;
        }

        try
        {
            detouring = true;
            deferredContext.ClearState();
            deferredContext.OutputMerger.SetTargets(probe.DepthStencil, [.. probe.RenderTargets.Select(target => target!)]);
            deferredContext.Rasterizer.SetViewport(0, 0, probe.Width, probe.Height, 0, 1);
            deferredContext.Rasterizer.State = worldRasterizerState;
            deferredContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            deferredContext.InputAssembler.InputLayout = worldInputLayout;
            deferredContext.InputAssembler.SetVertexBuffers(
                0,
                new VertexBufferBinding(worldVertexBuffer, Utilities.SizeOf<WorldVertex>(), 0)
            );
            deferredContext.HullShader.Set(null);
            deferredContext.DomainShader.Set(null);
            deferredContext.GeometryShader.Set(null);
            deferredContext.VertexShader.Set(worldVertexShader);
            deferredContext.VertexShader.SetConstantBuffer(0, worldConstantsBuffer);
            deferredContext.PixelShader.Set(worldPixelShader);
            deferredContext.PixelShader.SetConstantBuffer(0, worldConstantsBuffer);
            DrawDonorOpaqueTuple(controlViewProjection, triangleIndex);

            using var commandList = deferredContext.FinishCommandList(false);
            immediateContext.ExecuteCommandList(commandList, true);
        }
        finally
        {
            detouring = false;
            deferredContext.ClearState();
        }
    }

    private void OMSetDepthStencilStateDetour(nint context, nint depthStencilState, uint stencilReference)
    {
        try
        {
            if (!detouring)
            {
                lastDepthStencilState = DescribeDepthStencilState(depthStencilState, stencilReference);
                if (candidateActive)
                {
                    candidateDepthStencilState = lastDepthStencilState;
                    var transition = $"draw={candidateDrawCount}:{lastDepthStencilState}";
                    if (candidateDepthStencilTransitions.Count < 48 && candidateDepthStencilTransitions[^1] != transition)
                    {
                        candidateDepthStencilTransitions.Add(transition);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            log.Warning(exception, "G-buffer probe failed while tracking OMSetDepthStencilState.");
        }
        finally
        {
            omSetDepthStencilStateHook.Original(context, depthStencilState, stencilReference);
        }
    }

    private static string DescribeDepthStencilState(nint depthStencilState, uint stencilReference)
    {
        if (depthStencilState == 0)
        {
            return $"state=null,stencilRef={stencilReference}";
        }

        var state = new DepthStencilState(depthStencilState);
        try
        {
            var description = state.Description;
            return $"state=0x{depthStencilState:X},depth={description.IsDepthEnabled}/{description.DepthWriteMask}/{description.DepthComparison},"
                + $"stencil={description.IsStencilEnabled},read=0x{description.StencilReadMask:X2},write=0x{description.StencilWriteMask:X2},"
                + $"ref={stencilReference},front={FormatStencilFace(description.FrontFace)},"
                + $"back={FormatStencilFace(description.BackFace)}";
        }
        finally
        {
            state.NativePointer = nint.Zero;
            state.Dispose();
        }
    }

    private static string FormatStencilFace(DepthStencilOperationDescription face) =>
        $"{face.Comparison}/{face.FailOperation}/{face.DepthFailOperation}/{face.PassOperation}";

    private void DrawIndexedDetour(nint context, uint indexCount, uint startIndexLocation, int baseVertexLocation)
    {
        MarkCandidateDraw(context, indexed: true);
        drawIndexedHook.Original(context, indexCount, startIndexLocation, baseVertexLocation);
    }

    private void DrawDetour(nint context, uint vertexCount, uint startVertexLocation)
    {
        MarkCandidateDraw(context, indexed: false);
        drawHook.Original(context, vertexCount, startVertexLocation);
    }

    private void DrawIndexedInstancedDetour(
        nint context,
        uint indexCountPerInstance,
        uint instanceCount,
        uint startIndexLocation,
        int baseVertexLocation,
        uint startInstanceLocation
    )
    {
        MarkCandidateDraw(context, indexed: true);
        drawIndexedInstancedHook.Original(
            context,
            indexCountPerInstance,
            instanceCount,
            startIndexLocation,
            baseVertexLocation,
            startInstanceLocation
        );
    }

    private void DrawInstancedDetour(
        nint context,
        uint vertexCountPerInstance,
        uint instanceCount,
        uint startVertexLocation,
        uint startInstanceLocation
    )
    {
        MarkCandidateDraw(context, indexed: false);
        drawInstancedHook.Original(context, vertexCountPerInstance, instanceCount, startVertexLocation, startInstanceLocation);
    }

    private void MarkCandidateDraw(nint context, bool indexed)
    {
        if (detouring || context != immediateContextPointer)
        {
            return;
        }

        immediateDrawSerial++;
        drawsSinceTargetBinding++;
        if (candidateActive && configuration.EnableGBufferProbe)
        {
            candidateDrawCount++;
            if (indexed)
            {
                candidateIndexedDrawCount++;
            }
        }
    }

    private void ClearRenderTargetViewDetour(nint context, nint renderTargetView, float* color)
    {
        try
        {
            if (
                !detouring
                && context == immediateContextPointer
                && candidateActive
                && Array.IndexOf(candidateRenderTargets, renderTargetView) >= 0
            )
            {
                candidateDrawCount = 0;
                candidateIndexedDrawCount = 0;
            }
        }
        catch (Exception exception)
        {
            log.Warning(exception, "G-buffer probe failed while tracking ClearRenderTargetView.");
        }
        finally
        {
            clearRenderTargetViewHook.Original(context, renderTargetView, color);
        }
    }

    private void ClearDepthStencilViewDetour(nint context, nint depthStencilView, uint clearFlags, float depth, byte stencil)
    {
        try
        {
            if (!detouring && context == immediateContextPointer)
            {
                targetBindingHistory.Enqueue(
                    $"serial={immediateDrawSerial},previousDraws={drawsSinceTargetBinding},ClearDSV:dsv=0x{depthStencilView:X},"
                        + $"flags=0x{clearFlags:X},depth={depth},stencil={stencil}"
                );
                while (targetBindingHistory.Count > 32)
                {
                    targetBindingHistory.Dequeue();
                }

                if (depthStencilView != candidateDepthStencil)
                {
                    return;
                }

                if (gBufferConsumerTransitions.Count > 0 && gBufferConsumerSummaryLogCount++ < 1)
                {
                    log.Information($"G-buffer SRV consumers: {string.Join(" || ", gBufferConsumerTransitions)}.");
                }
                gBufferConsumerTransitions.Clear();

                if (completedCandidateExits > 0 && frameSummaryLogCount++ < 1)
                {
                    log.Information($"G-buffer probe frame summary: candidateExits={completedCandidateExits}.");
                }

                probeIssuedSinceDepthClear = false;
                candidateDrawCount = 0;
                candidateIndexedDrawCount = 0;
                nextCandidateOrdinal = 0;
                completedCandidateExits = 0;
            }
        }
        catch (Exception exception)
        {
            log.Warning(exception, "G-buffer probe failed while tracking ClearDepthStencilView.");
        }
        finally
        {
            clearDepthStencilViewHook.Original(context, depthStencilView, clearFlags, depth, stencil);
        }
    }

    private void ResetCandidate()
    {
        candidateActive = false;
        candidateDrawCount = 0;
        candidateIndexedDrawCount = 0;
        candidateWidth = 0;
        candidateHeight = 0;
        candidateRenderTargets = [];
        candidateDepthStencilTransitions.Clear();
        candidateUavTransitions.Clear();
    }
}
