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
    private const int DetailedExitLogLimit = 16;

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
    private readonly RasterizerState rasterizerState;
    private readonly RasterizerState worldRasterizerState;
    private readonly BlendState depthOnlyBlendState;
    private readonly BlendState target0RgbBlendState;
    private readonly BlendState target2RgbBlendState;
    private readonly BlendState worldTriangleBlendState;
    private readonly DepthStencilState depthWriteState;
    private readonly DepthStencilState worldDepthWriteState;
    private readonly DepthStencilState noDepthWriteState;

    private readonly Hook<OMSetRenderTargetsDelegate> omSetRenderTargetsHook;
    private readonly Hook<OMSetRenderTargetsAndUnorderedAccessViewsDelegate> omSetRenderTargetsAndUavsHook;
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
    private bool probeIssuedSinceDepthClear;
    private bool detouring;
    private bool lastEnabled;
    private GBufferProbeMode lastMode;
    private int lastConfiguredOrdinal = -1;
    private int candidateLogCount;
    private int candidateExitLogCount;
    private int frameSummaryLogCount;
    private int injectionCount;
    private bool disposed;
    private readonly long controllerStartTimestamp = Stopwatch.GetTimestamp();
    private WorldVertex[]? worldTriangleVertices;
    private GBufferWorldTriangleMarker[]? worldTriangleMarkers;

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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WorldConstants
    {
        public Matrix ViewProjection;
        public Vector4 Albedo;
        public Vector4 Diagnostic;
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
            string nextTargets
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
            };

            struct VertexInput
            {
                float3 Position : POSITION;
                float3 Normal : NORMAL;
                uint VertexId : SV_VertexID;
            };

            struct PixelInput
            {
                float4 Position : SV_POSITION;
                float3 WorldNormal : NORMAL;
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
            ]
        );
        worldVertexBuffer = new D3D11Buffer(
            device,
            Utilities.SizeOf<WorldVertex>() * 18,
            ResourceUsage.Default,
            BindFlags.VertexBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0
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

        var depthDescription = DepthStencilStateDescription.Default();
        depthDescription.IsDepthEnabled = true;
        depthDescription.DepthWriteMask = DepthWriteMask.All;
        depthDescription.DepthComparison = Comparison.Always;
        depthDescription.IsStencilEnabled = false;
        depthWriteState = new DepthStencilState(device, depthDescription);

        depthDescription.DepthComparison = Comparison.GreaterEqual;
        worldDepthWriteState = new DepthStencilState(device, depthDescription);

        depthDescription.IsDepthEnabled = false;
        depthDescription.DepthWriteMask = DepthWriteMask.Zero;
        noDepthWriteState = new DepthStencilState(device, depthDescription);

        var vtable = *(nint**)immediateContextPointer;
        omSetRenderTargetsHook = gameInteropProvider.HookFromAddress<OMSetRenderTargetsDelegate>(vtable[33], OMSetRenderTargetsDetour);
        omSetRenderTargetsAndUavsHook = gameInteropProvider.HookFromAddress<OMSetRenderTargetsAndUnorderedAccessViewsDelegate>(
            vtable[34],
            OMSetRenderTargetsAndUavsDetour
        );
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

    public void Enable()
    {
        omSetRenderTargetsHook.Enable();
        omSetRenderTargetsAndUavsHook.Enable();
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
        worldTriangleVertices = null;
        worldTriangleMarkers = null;
        log.Information(
            $"G-buffer probe {(lastEnabled ? "enabled" : "disabled")}: mode={lastMode}, candidateExitOrdinal={lastConfiguredOrdinal}."
        );
    }

    public bool TryGetWorldTriangleMarkers(out GBufferWorldTriangleMarker[] markers)
    {
        markers = [];
        if (!configuration.EnableGBufferProbe || configuration.GBufferProbeMode != GBufferProbeMode.WorldTriangle)
        {
            return false;
        }

        var snapshot = Volatile.Read(ref worldTriangleMarkers);
        if (snapshot is not { Length: > 0 })
        {
            return false;
        }

        markers = snapshot;
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
        omSetRenderTargetsAndUavsHook.Dispose();
        omSetRenderTargetsHook.Dispose();

        noDepthWriteState.Dispose();
        worldDepthWriteState.Dispose();
        depthWriteState.Dispose();
        worldTriangleBlendState.Dispose();
        target2RgbBlendState.Dispose();
        target0RgbBlendState.Dispose();
        depthOnlyBlendState.Dispose();
        worldRasterizerState.Dispose();
        rasterizerState.Dispose();
        worldConstantsBuffer.Dispose();
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
                pendingProbe = TrackTargetChange(numRenderTargetViews, renderTargetViews, depthStencilView);
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
            candidateDrawCount = 0;
            candidateIndexedDrawCount = 0;
        }

        candidateDepthStencil = depthStencilView;
        candidateWidth = match.Width;
        candidateHeight = match.Height;
        candidateRenderTargets = new nint[numViews];
        for (var index = 0; index < numViews; index++)
        {
            candidateRenderTargets[index] = renderTargetViews[index];
        }

        if (candidateLogCount++ < 5)
        {
            log.Information(
                $"G-buffer candidate begin: ordinal={candidateOrdinal}, matched={match.MatchedCount}, views={numViews}, size={match.Width}x{match.Height}, viewport={candidateViewport}, dsv=0x{depthStencilView:X}."
            );
        }

        return pendingProbe;
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

        if (candidateExitLogCount++ < DetailedExitLogLimit)
        {
            log.Information(
                $"G-buffer candidate exit: ordinal={candidateOrdinal}, draws={candidateDrawCount}, indexedDraws={candidateIndexedDrawCount}, "
                    + $"rtvs=[{string.Join(",", candidateRenderTargets.Select(pointer => $"0x{pointer:X}"))}], dsv=0x{candidateDepthStencil:X}, "
                    + $"viewport={candidateViewport}, startMs={startMilliseconds:F3}, endMs={endMilliseconds:F3}, "
                    + $"durationMs={endMilliseconds - startMilliseconds:F3}, next={nextTargets}, selected={selected}."
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
            nextTargets
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

    private void TryIssueProbe(PendingProbe probe)
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
            LogProbeResult(probe, "draw skipped because the candidate was incomplete");
            return;
        }

        try
        {
            var (blendState, depthState) = configuration.GBufferProbeMode switch
            {
                GBufferProbeMode.DepthOnly => (depthOnlyBlendState, depthWriteState),
                GBufferProbeMode.Target0Rgb => (target0RgbBlendState, noDepthWriteState),
                GBufferProbeMode.Target2Rgb => (target2RgbBlendState, noDepthWriteState),
                GBufferProbeMode.WorldTriangle => (worldTriangleBlendState, worldDepthWriteState),
                _ => throw new InvalidOperationException($"Unsupported G-buffer probe mode: {configuration.GBufferProbeMode}"),
            };

            var isWorldTriangle = configuration.GBufferProbeMode == GBufferProbeMode.WorldTriangle;
            Matrix controlViewProjection = default;
            Matrix sceneViewProjection = default;
            if (isWorldTriangle && !TryUpdateWorldTriangleResources(out controlViewProjection, out sceneViewProjection))
            {
                LogProbeResult(probe, "world triangle skipped because the active scene camera was unavailable");
                return;
            }

            detouring = true;
            deferredContext.ClearState();
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
                DrawWorldTriangleDiagnostics(controlViewProjection, sceneViewProjection);
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
            probeIssuedSinceDepthClear = true;
            injectionCount++;
            var geometry = isWorldTriangle ? "world triangle" : $"{ProbeSize}x{ProbeSize}";
            LogProbeResult(probe, $"draw #{injectionCount} issued, geometry={geometry}, targets={probe.RenderTargets.Length}");
        }
        finally
        {
            detouring = false;
            deferredContext.ClearState();
        }
    }

    private void LogProbeResult(PendingProbe probe, string result)
    {
        if (injectionCount <= 5 || injectionCount % 300 == 0)
        {
            log.Information(
                $"G-buffer probe: ordinal={probe.Ordinal}, mode={configuration.GBufferProbeMode}, draws={probe.DrawCount}, "
                    + $"indexedDraws={probe.IndexedDrawCount}, viewport={probe.Viewport}, next={probe.NextTargets}, result={result}."
            );
        }
    }

    private bool TryUpdateWorldTriangleResources(out Matrix controlViewProjection, out Matrix sceneViewProjection)
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
            var inverseView = viewMatrix;
            inverseView.Invert();

            var cameraPosition = new Vector3(renderCamera->Origin.X, renderCamera->Origin.Y, renderCamera->Origin.Z);
            var right = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, inverseView));
            var up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inverseView));
            var forward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, inverseView));
            var cameraViewPosition = Vector3.TransformCoordinate(cameraPosition, viewMatrix);
            var forwardViewPosition = Vector3.TransformCoordinate(cameraPosition + forward, viewMatrix);
            if (forwardViewPosition.Z <= cameraViewPosition.Z)
            {
                forward = -forward;
            }

            var definitions = new (string Label, float Right, float Up, Vector4 Color)[]
            {
                ("C/NoDepth/T", -2.2f, 1.25f, new Vector4(1f, 0.1f, 0.1f, 1f)),
                ("S/NoDepth/T", 0f, 1.25f, new Vector4(0.1f, 1f, 0.1f, 1f)),
                ("C/NoDepth/Raw", 2.2f, 1.25f, new Vector4(0.1f, 0.3f, 1f, 1f)),
                ("C/Always/T", -2.2f, -0.75f, new Vector4(1f, 1f, 0.1f, 1f)),
                ("C/GreaterEqual/T", 0f, -0.75f, new Vector4(1f, 0.1f, 1f, 1f)),
                ("S/GreaterEqual/T", 2.2f, -0.75f, new Vector4(0.1f, 1f, 1f, 1f)),
            };

            var vertices = new List<WorldVertex>(definitions.Length * 3);
            var markers = new List<GBufferWorldTriangleMarker>(definitions.Length);
            foreach (var definition in definitions)
            {
                var center = cameraPosition + forward * 5f + right * definition.Right + up * definition.Up;
                var p0 = center - right * 0.9f - up * 0.75f - forward * 0.45f;
                var p1 = center + right * 0.9f - up * 0.75f + forward * 0.45f;
                var p2 = center + up * 0.9f;
                var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
                if (Vector3.Dot(normal, cameraPosition - center) < 0)
                {
                    normal = -normal;
                }

                vertices.Add(new WorldVertex { Position = p0, Normal = normal });
                vertices.Add(new WorldVertex { Position = p1, Normal = normal });
                vertices.Add(new WorldVertex { Position = p2, Normal = normal });
                markers.Add(
                    new GBufferWorldTriangleMarker(
                        definition.Label,
                        new System.Numerics.Vector3(center.X, center.Y, center.Z),
                        new System.Numerics.Vector4(definition.Color.X, definition.Color.Y, definition.Color.Z, definition.Color.W)
                    )
                );
            }

            worldTriangleVertices = [.. vertices];
            worldTriangleMarkers = [.. markers];

            log.Information(
                "World-triangle diagnostics initialized: matrixSources=Control.ViewProjectionMatrix|Scene.Camera.ViewMatrix*Render.Camera.ProjectionMatrix, "
                    + $"camera={FormatVector(cameraPosition)}, matrixMaxDelta={GetMatrixMaxDelta(controlViewProjection, sceneViewProjection):G4}, "
                    + "variants=C/NoDepth/T,S/NoDepth/T,C/NoDepth/Raw,C/Always/T,C/GreaterEqual/T,S/GreaterEqual/T, cull=None."
            );
        }

        deferredContext.UpdateSubresource(worldTriangleVertices, worldVertexBuffer);
        return true;
    }

    private void DrawWorldTriangleDiagnostics(Matrix controlViewProjection, Matrix sceneViewProjection)
    {
        var variants = new (Matrix ViewProjection, bool Transpose, DepthStencilState DepthState, Vector4 Albedo)[]
        {
            (controlViewProjection, true, noDepthWriteState, new Vector4(1f, 0.1f, 0.1f, 1f)),
            (sceneViewProjection, true, noDepthWriteState, new Vector4(0.1f, 1f, 0.1f, 1f)),
            (controlViewProjection, false, noDepthWriteState, new Vector4(0.1f, 0.3f, 1f, 1f)),
            (controlViewProjection, true, depthWriteState, new Vector4(1f, 1f, 0.1f, 1f)),
            (controlViewProjection, true, worldDepthWriteState, new Vector4(1f, 0.1f, 1f, 1f)),
            (sceneViewProjection, true, worldDepthWriteState, new Vector4(0.1f, 1f, 1f, 1f)),
        };

        for (var index = 0; index < variants.Length; index++)
        {
            var variant = variants[index];
            var constants = new WorldConstants
            {
                ViewProjection = variant.ViewProjection,
                Albedo = variant.Albedo,
                Diagnostic = Vector4.Zero,
            };
            if (variant.Transpose)
            {
                constants.ViewProjection.Transpose();
            }

            deferredContext.OutputMerger.SetDepthStencilState(variant.DepthState);
            deferredContext.UpdateSubresource(ref constants, worldConstantsBuffer);
            deferredContext.Draw(3, index * 3);
        }

        var clipControlConstants = new WorldConstants
        {
            ViewProjection = Matrix.Identity,
            Albedo = new Vector4(1f, 0.35f, 0.05f, 1f),
            Diagnostic = new Vector4(1f, 0f, 0f, 0f),
        };
        deferredContext.OutputMerger.SetDepthStencilState(noDepthWriteState);
        deferredContext.UpdateSubresource(ref clipControlConstants, worldConstantsBuffer);
        deferredContext.Draw(3, 0);
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
        if (!detouring && context == immediateContextPointer && candidateActive && configuration.EnableGBufferProbe)
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
            if (!detouring && context == immediateContextPointer && depthStencilView == candidateDepthStencil)
            {
                if (completedCandidateExits > 0 && frameSummaryLogCount++ < 5)
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
    }
}
