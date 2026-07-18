#if DEBUG
using System.Diagnostics;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.Interop;

namespace EventHorizon.Integration.Debug;

/// <summary>
/// A deliberately narrow probe around the sole caller of ModelRenderer.OnRenderMaterial.
/// The caller expands one model geometry/material entry and records render commands into
/// the current thread's Graphics::Kernel::Context.
/// </summary>
internal sealed unsafe class MaterialSubmissionContainerProbe : IDisposable
{
    private const string MaterialBuilderSignature =
        "40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ?? 48 81 EC 08 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 65 48 8B 04 25";
    private const int TargetMaterialIndex = 2;
    private const int GeometryEntrySize = 0x24;
    private const int Params2Size = 0x48;
    private const int MaxEvents = 128;
    private const int MaxAllocationsPerBuild = 64;
    private const int MaxHashBytes = 1024 * 1024;
    private const string NativeCaller = "ffxiv_dx11.exe+0x281D91";

    [ThreadStatic]
    private static BuildScope? activeScope;

    private readonly Hook<MaterialBuilderDelegate> materialBuilderHook;
    private readonly Hook<OnRenderMaterialDelegate> onRenderMaterialHook;
    private readonly Hook<AllocateCommandDelegate> allocateCommandHook;
    private readonly Lock stateLock = new();
    private readonly List<MaterialSubmissionProbeEvent> events = [];
    private nint targetModel;
    private long nextBuildCycle;
    private bool disposed;

    private delegate nint MaterialBuilderDelegate(
        ModelRenderer* modelRenderer,
        ModelRenderer.OnRenderModelParams* param,
        ModelResourceHandle* modelResource,
        uint geometryIndex,
        int flags
    );

    private delegate ushort* OnRenderMaterialDelegate(
        ModelRenderer* modelRenderer,
        ModelRenderer.OnRenderMaterialParams2* param,
        Material* material,
        uint materialIndex
    );

    private delegate void* AllocateCommandDelegate(Context* context, ulong size);

    public MaterialSubmissionContainerProbe(IGameInteropProvider gameInteropProvider)
    {
        materialBuilderHook = gameInteropProvider.HookFromSignature<MaterialBuilderDelegate>(
            MaterialBuilderSignature,
            MaterialBuilderDetour
        );
        onRenderMaterialHook = gameInteropProvider.HookFromAddress<OnRenderMaterialDelegate>(
            (nint)ModelRenderer.MemberFunctionPointers.OnRenderMaterial,
            OnRenderMaterialDetour
        );
        allocateCommandHook = gameInteropProvider.HookFromAddress<AllocateCommandDelegate>(
            (nint)Context.MemberFunctionPointers.AllocateCommand,
            AllocateCommandDetour
        );
        materialBuilderHook.Enable();
        onRenderMaterialHook.Enable();
        allocateCommandHook.Enable();
    }

    public void Arm(nint model)
    {
        lock (stateLock)
            events.Clear();
        Interlocked.Exchange(ref targetModel, model);
    }

    public MaterialSubmissionProbeEvent[] StopAndTake()
    {
        lock (stateLock)
        {
            Interlocked.Exchange(ref targetModel, 0);
            var result = events.ToArray();
            events.Clear();
            return result;
        }
    }

    public void Stop()
    {
        Interlocked.Exchange(ref targetModel, 0);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Stop();
        allocateCommandHook.Dispose();
        onRenderMaterialHook.Dispose();
        materialBuilderHook.Dispose();
    }

    private nint MaterialBuilderDetour(
        ModelRenderer* modelRenderer,
        ModelRenderer.OnRenderModelParams* param,
        ModelResourceHandle* modelResource,
        uint geometryIndex,
        int flags
    )
    {
        var model = param == null ? null : param->Model;
        if (model == null || modelResource == null || !IsTargetModel(model))
            return materialBuilderHook.Original(modelRenderer, param, modelResource, geometryIndex, flags);

        var geometryEntries = *(byte**)((byte*)modelResource + 0xE8);
        if (geometryEntries == null)
            return materialBuilderHook.Original(modelRenderer, param, modelResource, geometryIndex, flags);

        var geometryEntry = geometryEntries + geometryIndex * GeometryEntrySize;
        var materialIndex = *(ushort*)(geometryEntry + 8);
        if (materialIndex != TargetMaterialIndex || model->Materials == null || materialIndex >= model->MaterialCount)
        {
            return materialBuilderHook.Original(modelRenderer, param, modelResource, geometryIndex, flags);
        }

        var material = model->Materials[materialIndex];
        if (material == null || !IsCharacterTransparency(material))
            return materialBuilderHook.Original(modelRenderer, param, modelResource, geometryIndex, flags);

        var threadLocals = ThreadLocals.ThreadLocalInstance();
        var context = threadLocals == null ? null : threadLocals->GraphicsKernelContext;
        var scope = new BuildScope(
            Interlocked.Increment(ref nextBuildCycle),
            Stopwatch.GetTimestamp(),
            Environment.CurrentManagedThreadId,
            modelRenderer,
            param,
            model,
            modelResource,
            geometryIndex,
            geometryEntry,
            material,
            context,
            CaptureContext(context),
            ComputeFnv1A64(new ReadOnlySpan<byte>(geometryEntry, GeometryEntrySize))
        );

        var previousScope = activeScope;
        activeScope = scope;
        try
        {
            return materialBuilderHook.Original(modelRenderer, param, modelResource, geometryIndex, flags);
        }
        finally
        {
            activeScope = previousScope;
            try
            {
                CompleteScope(scope);
            }
            catch (Exception exception)
            {
                DebugFileLog.Error("MaterialSubmissionProbe", exception, "Failed to complete material submission observation");
            }
        }
    }

    private ushort* OnRenderMaterialDetour(
        ModelRenderer* modelRenderer,
        ModelRenderer.OnRenderMaterialParams2* param,
        Material* material,
        uint materialIndex
    )
    {
        var scope = activeScope;
        var observe = scope != null && materialIndex == TargetMaterialIndex && material == scope.Material && param != null;
        var before = observe ? new ReadOnlySpan<byte>(param, Params2Size).ToArray() : null;
        var result = onRenderMaterialHook.Original(modelRenderer, param, material, materialIndex);
        try
        {
            if (observe)
            {
                var after = new ReadOnlySpan<byte>(param, Params2Size).ToArray();
                scope!.MaterialObservation = new MaterialObservation(
                    (nint)param,
                    (nint)result,
                    ComputeFnv1A64(before!),
                    ComputeFnv1A64(after),
                    FormatChangedOffsets(before!, after),
                    FormatQwords(before!),
                    FormatQwords(after)
                );
            }
        }
        catch (Exception exception)
        {
            DebugFileLog.Error("MaterialSubmissionProbe", exception, "Failed to observe OnRenderMaterial output");
        }
        return result;
    }

    private void* AllocateCommandDetour(Context* context, ulong size)
    {
        var result = allocateCommandHook.Original(context, size);
        var scope = activeScope;
        if (scope != null && scope.Context == context && result != null && scope.Allocations.Count < MaxAllocationsPerBuild)
        {
            scope.Allocations.Add(new PendingAllocation((nint)result, size));
        }
        return result;
    }

    private void CompleteScope(BuildScope scope)
    {
        var after = CaptureContext(scope.Context);
        var allocations = scope
            .Allocations.Select(allocation => new CommandAllocationSnapshot(
                allocation.Address,
                allocation.Size,
                HashRange((void*)allocation.Address, allocation.Size)
            ))
            .ToArray();
        var commandDelta = CaptureArenaDelta(
            scope.Before.CommandAllocationBase,
            scope.Before.CommandAllocationUsedSize,
            after.CommandAllocationBase,
            after.CommandAllocationUsedSize
        );
        var payloadDelta = CaptureArenaDelta(
            (nint)scope.Before.AllocationBase,
            scope.Before.AllocationUsedSize,
            (nint)after.AllocationBase,
            after.AllocationUsedSize
        );
        var probeEvent = new MaterialSubmissionProbeEvent(
            scope.BuildCycle,
            scope.Timestamp,
            scope.ThreadId,
            NativeCaller,
            (nint)scope.ModelRenderer,
            (nint)scope.ModelParams,
            (nint)scope.Model,
            (nint)scope.ModelResource,
            scope.GeometryIndex,
            (nint)scope.GeometryEntry,
            scope.GeometryHash,
            (nint)scope.Material,
            (nint)scope.Material->MaterialResourceHandle,
            (nint)scope.Context,
            scope.Before,
            after,
            commandDelta,
            payloadDelta,
            allocations,
            scope.MaterialObservation
        );

        lock (stateLock)
        {
            if (Interlocked.CompareExchange(ref targetModel, 0, 0) == (nint)scope.Model && events.Count < MaxEvents)
                events.Add(probeEvent);
        }
    }

    private bool IsTargetModel(Model* model) => Interlocked.CompareExchange(ref targetModel, 0, 0) == (nint)model;

    private static bool IsCharacterTransparency(Material* material)
    {
        var resource = material->MaterialResourceHandle;
        return resource != null && resource->ShpkName.ToString().Equals("charactertransparency.shpk", StringComparison.OrdinalIgnoreCase);
    }

    private static ContextSnapshot CaptureContext(Context* context) =>
        context == null
            ? default
            : new ContextSnapshot(
                (nint)context->CommandAllocationBase,
                context->CommandAllocationUsedSize,
                context->AllocationBase,
                context->AllocationUsedSize,
                context->SortKey,
                context->ViewIndex,
                context->CurrentSubViewIndex
            );

    private static ArenaDelta CaptureArenaDelta(nint beforeBase, ulong beforeUsed, nint afterBase, ulong afterUsed)
    {
        if (beforeBase == 0 || beforeBase != afterBase || afterUsed < beforeUsed)
            return new ArenaDelta(0, 0, null, beforeBase == afterBase ? "invalid-range" : "base-changed");

        var size = afterUsed - beforeUsed;
        return new ArenaDelta(
            beforeBase + checked((nint)beforeUsed),
            size,
            HashRange((void*)(beforeBase + checked((nint)beforeUsed)), size),
            size > MaxHashBytes ? "hash-truncated" : "ok"
        );
    }

    private static ulong? HashRange(void* address, ulong size)
    {
        if (address == null || size == 0)
            return null;
        var bytes = checked((int)Math.Min(size, MaxHashBytes));
        return ComputeFnv1A64(new ReadOnlySpan<byte>(address, bytes));
    }

    private static ulong ComputeFnv1A64(ReadOnlySpan<byte> bytes)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= prime;
        }
        return hash;
    }

    private static string FormatChangedOffsets(byte[] before, byte[] after) =>
        string.Join(
            ',',
            Enumerable
                .Range(0, Math.Min(before.Length, after.Length))
                .Where(index => before[index] != after[index])
                .Select(index => $"0x{index:X}")
        );

    private static string FormatQwords(byte[] bytes) =>
        string.Join(
            ',',
            Enumerable.Range(0, bytes.Length / 8).Select(index => $"+0x{index * 8:X}=0x{BitConverter.ToUInt64(bytes, index * 8):X}")
        );

    private sealed class BuildScope(
        long buildCycle,
        long timestamp,
        int threadId,
        ModelRenderer* modelRenderer,
        ModelRenderer.OnRenderModelParams* modelParams,
        Model* model,
        ModelResourceHandle* modelResource,
        uint geometryIndex,
        byte* geometryEntry,
        Material* material,
        Context* context,
        ContextSnapshot before,
        ulong geometryHash
    )
    {
        public long BuildCycle { get; } = buildCycle;
        public long Timestamp { get; } = timestamp;
        public int ThreadId { get; } = threadId;
        public ModelRenderer* ModelRenderer { get; } = modelRenderer;
        public ModelRenderer.OnRenderModelParams* ModelParams { get; } = modelParams;
        public Model* Model { get; } = model;
        public ModelResourceHandle* ModelResource { get; } = modelResource;
        public uint GeometryIndex { get; } = geometryIndex;
        public byte* GeometryEntry { get; } = geometryEntry;
        public Material* Material { get; } = material;
        public Context* Context { get; } = context;
        public ContextSnapshot Before { get; } = before;
        public ulong GeometryHash { get; } = geometryHash;
        public List<PendingAllocation> Allocations { get; } = [];
        public MaterialObservation? MaterialObservation { get; set; }
    }

    private readonly record struct PendingAllocation(nint Address, ulong Size);
}

internal sealed record MaterialSubmissionProbeEvent(
    long BuildCycle,
    long Timestamp,
    int ThreadId,
    string NativeCaller,
    nint ModelRenderer,
    nint ModelParams,
    nint Model,
    nint ModelResource,
    uint GeometryIndex,
    nint GeometryEntry,
    ulong GeometryHash,
    nint Material,
    nint MaterialResource,
    nint Context,
    ContextSnapshot Before,
    ContextSnapshot After,
    ArenaDelta CommandDelta,
    ArenaDelta PayloadDelta,
    IReadOnlyList<CommandAllocationSnapshot> CommandAllocations,
    MaterialObservation? MaterialObservation
);

internal readonly record struct ContextSnapshot(
    nint CommandAllocationBase,
    ulong CommandAllocationUsedSize,
    ulong AllocationBase,
    ulong AllocationUsedSize,
    uint SortKey,
    int ViewIndex,
    byte SubViewIndex
);

internal readonly record struct ArenaDelta(nint Address, ulong Size, ulong? Hash, string Status);

internal readonly record struct CommandAllocationSnapshot(nint Address, ulong Size, ulong? Hash);

internal sealed record MaterialObservation(
    nint Params,
    nint ReturnValue,
    ulong BeforeHash,
    ulong AfterHash,
    string ChangedOffsets,
    string BeforeQwords,
    string AfterQwords
);
#endif
