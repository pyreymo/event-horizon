#if DEBUG
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.Interop;
using Underpaint;
using Underpaint.Internal;

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
    private const int MaxPushedCommandsPerBuild = 16;
    private const int MaxConsumptionEvents = 128;
    private const int MaxExecutionEvents = 128;
    private const int MaxHashBytes = 1024 * 1024;
    private const string PrepareDrawStateSignature = "40 56 41 56 48 83 EC 48 80 7A";
    private const string NativeCaller = "ffxiv_dx11.exe+0x281D91";
    private const string ExpandPassesSignature = "44 89 4C 24 ?? 44 89 44 24 ?? 53 56 57 41 54 41 55";

    [ThreadStatic]
    private static BuildScope? activeScope;

    private readonly Hook<MaterialBuilderDelegate> materialBuilderHook;
    private readonly Hook<OnRenderMaterialDelegate> onRenderMaterialHook;
    private readonly Hook<AllocateCommandDelegate> allocateCommandHook;
    private readonly Hook<PushBackCommandDelegate> pushBackCommandHook;
    private readonly Hook<PreprocessCommandsDelegate> preprocessCommandsHook;
    private readonly Hook<PrepareDrawStateDelegate> prepareDrawStateHook;
    private readonly Hook<ExpandPassesDelegate> expandPassesHook;
    private readonly UnderpaintRenderer? underpaint;
    private readonly Lock stateLock = new();
    private readonly List<MaterialSubmissionProbeEvent> events = [];
    private readonly List<CommandConsumptionSnapshot> consumptionEvents = [];
    private readonly List<CommandExecutionSnapshot> executionEvents = [];
    private readonly ConcurrentDictionary<nint, ProducedCommandIdentity> producedCommands = new();
    private nint targetModel;
    private int duplicateMainSubmissionArmed;
    private int customGeometrySubmissionArmed;
    private long nextBuildCycle;
    private NativeGeometry? customGeometry;
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

    private delegate void PushBackCommandDelegate(Context* context, void* command);

    private delegate void PreprocessCommandsDelegate(
        ImmediateContext* immediateContext,
        RenderCommandBufferGroup* renderCommands,
        uint renderCommandCount
    );

    private delegate nint PrepareDrawStateDelegate(ImmediateContext* immediateContext, byte* state, int flags);

    private delegate nint ExpandPassesDelegate(
        ModelRenderer* modelRenderer,
        ModelRenderer.OnRenderMaterialParams2* param,
        int vertexCount,
        int startIndex,
        int indexCount
    );

    public MaterialSubmissionContainerProbe(IGameInteropProvider gameInteropProvider, UnderpaintRenderer? underpaint)
    {
        this.underpaint = underpaint;
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
        pushBackCommandHook = gameInteropProvider.HookFromAddress<PushBackCommandDelegate>(
            (nint)Context.MemberFunctionPointers.PushBackCommand,
            PushBackCommandDetour
        );
        preprocessCommandsHook = gameInteropProvider.HookFromAddress<PreprocessCommandsDelegate>(
            (nint)ImmediateContext.MemberFunctionPointers.PreprocessCommands,
            PreprocessCommandsDetour
        );
        prepareDrawStateHook = gameInteropProvider.HookFromSignature<PrepareDrawStateDelegate>(
            PrepareDrawStateSignature,
            PrepareDrawStateDetour
        );
        expandPassesHook = gameInteropProvider.HookFromSignature<ExpandPassesDelegate>(ExpandPassesSignature, ExpandPassesDetour);
        materialBuilderHook.Enable();
        onRenderMaterialHook.Enable();
        allocateCommandHook.Enable();
        pushBackCommandHook.Enable();
        preprocessCommandsHook.Enable();
        prepareDrawStateHook.Enable();
        expandPassesHook.Enable();
    }

    public void Arm(nint model, bool duplicateMainSubmission = false, bool customGeometrySubmission = false)
    {
        lock (stateLock)
        {
            events.Clear();
            consumptionEvents.Clear();
            executionEvents.Clear();
        }
        producedCommands.Clear();
        Interlocked.Exchange(ref duplicateMainSubmissionArmed, duplicateMainSubmission ? 1 : 0);
        Interlocked.Exchange(ref customGeometrySubmissionArmed, customGeometrySubmission ? 1 : 0);
        Interlocked.Exchange(ref targetModel, model);
    }

    public MaterialSubmissionProbeCapture StopAndTake()
    {
        lock (stateLock)
        {
            Interlocked.Exchange(ref targetModel, 0);
            var result = new MaterialSubmissionProbeCapture(events.ToArray(), consumptionEvents.ToArray(), executionEvents.ToArray());
            events.Clear();
            consumptionEvents.Clear();
            executionEvents.Clear();
            producedCommands.Clear();
            return result;
        }
    }

    public void Stop()
    {
        Interlocked.Exchange(ref duplicateMainSubmissionArmed, 0);
        Interlocked.Exchange(ref customGeometrySubmissionArmed, 0);
        Interlocked.Exchange(ref targetModel, 0);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Stop();
        prepareDrawStateHook.Dispose();
        expandPassesHook.Dispose();
        preprocessCommandsHook.Dispose();
        pushBackCommandHook.Dispose();
        allocateCommandHook.Dispose();
        onRenderMaterialHook.Dispose();
        materialBuilderHook.Dispose();
        customGeometry?.Dispose();
    }

    private nint ExpandPassesDetour(
        ModelRenderer* modelRenderer,
        ModelRenderer.OnRenderMaterialParams2* param,
        int vertexCount,
        int startIndex,
        int indexCount
    )
    {
        var result = expandPassesHook.Original(modelRenderer, param, vertexCount, startIndex, indexCount);
        var scope = activeScope;
        var context = scope?.Context;
        if (
            scope == null
            || context == null
            || context->ViewIndex != 30
            || context->CurrentSubViewIndex != 11
            || Interlocked.CompareExchange(ref customGeometrySubmissionArmed, 0, 1) != 1
        )
        {
            return result;
        }

        if (underpaint == null)
        {
            DebugFileLog.Information("MaterialSubmissionProbe", "Underpaint native geometry backend is unavailable");
            return result;
        }

        try
        {
            customGeometry ??= underpaint.CreateNativeGeometry(
                [new Vector3(-0.75f, 0f, 0f), new Vector3(0.75f, 0f, 0f), new Vector3(0f, 1.5f, 0f)],
                [0, 1, 2]
            );
            var submission = underpaint.SubmitNativeGeometry(
                (nint)modelRenderer,
                (nint)param,
                customGeometry,
                (renderer, parameters, vertices, firstIndex, indices) =>
                    expandPassesHook.Original(
                        (ModelRenderer*)renderer,
                        (ModelRenderer.OnRenderMaterialParams2*)parameters,
                        vertices,
                        firstIndex,
                        indices
                    )
            );

            DebugFileLog.Information(
                "MaterialSubmissionProbe",
                "CustomGeometrySubmission Cycle={Cycle} Thread={Thread} Context=0x{Context:X} VB=0x{VB:X} VBResource=0x{VBResource:X} IB=0x{IB:X} IBResource=0x{IBResource:X} VertexDeclaration=0x{VertexDeclaration:X} OriginalRange={OriginalVertices}/{OriginalStart}/{OriginalIndices} CustomRange={CustomVertices}/0/{CustomIndices} Result=0x{Result:X}",
                scope.BuildCycle,
                scope.ThreadId,
                submission.Context,
                submission.VertexBuffer,
                submission.VertexBufferResource,
                submission.IndexBuffer,
                submission.IndexBufferResource,
                submission.VertexDeclaration,
                vertexCount,
                startIndex,
                indexCount,
                submission.VertexCount,
                submission.IndexCount,
                submission.BuilderResult
            );
        }
        catch (Exception exception)
        {
            DebugFileLog.Error("MaterialSubmissionProbe", exception, "Underpaint native geometry submission failed");
        }
        return result;
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
        if (model == null || modelResource == null)
            return materialBuilderHook.Original(modelRenderer, param, modelResource, geometryIndex, flags);

        if (!IsTargetModel(model))
            return materialBuilderHook.Original(modelRenderer, param, modelResource, geometryIndex, flags);

        var geometryEntries = *(byte**)((byte*)modelResource + 0xE8);
        if (geometryEntries == null)
            return materialBuilderHook.Original(modelRenderer, param, modelResource, geometryIndex, flags);

        var geometryEntry = geometryEntries + geometryIndex * GeometryEntrySize;
        var materialIndex = *(ushort*)(geometryEntry + 8);
        if (model->Materials == null || materialIndex >= model->MaterialCount)
        {
            return materialBuilderHook.Original(modelRenderer, param, modelResource, geometryIndex, flags);
        }

        var material = model->Materials[materialIndex];
        if (material == null || materialIndex != TargetMaterialIndex || !IsCharacterTransparency(material))
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
            materialIndex,
            context,
            CaptureContext(context),
            ComputeFnv1A64(new ReadOnlySpan<byte>(geometryEntry, GeometryEntrySize))
        );

        var previousScope = activeScope;
        activeScope = scope;
        try
        {
            var result = materialBuilderHook.Original(modelRenderer, param, modelResource, geometryIndex, flags);
            scope.BuilderResult = result;
            if (
                scope.PushedCommands.Any(command => command.ViewIndex == 30 && command.SubViewIndex == 12)
                && Interlocked.CompareExchange(ref duplicateMainSubmissionArmed, 0, 1) == 1
            )
            {
                scope.DuplicateApplied = true;
                scope.DuplicateStartPushIndex = scope.PushedCommands.Count;
                scope.DuplicateBoundary = CaptureContext(context);
                scope.DuplicateBuilderResult = materialBuilderHook.Original(modelRenderer, param, modelResource, geometryIndex, flags);
            }
            return result;
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
        var observe = scope != null && materialIndex == scope.MaterialIndex && material == scope.Material && param != null;
        var before = observe ? new ReadOnlySpan<byte>(param, Params2Size).ToArray() : null;
        var result = onRenderMaterialHook.Original(modelRenderer, param, material, materialIndex);
        try
        {
            if (observe)
            {
                var after = new ReadOnlySpan<byte>(param, Params2Size).ToArray();
                scope!.MaterialCallCount++;
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

    private void PushBackCommandDetour(Context* context, void* command)
    {
        pushBackCommandHook.Original(context, command);
        var scope = activeScope;
        if (scope == null || scope.Context != context || command == null || scope.PushedCommands.Count >= MaxPushedCommandsPerBuild)
        {
            return;
        }

        var address = (nint)command;
        var allocationSize = scope.Allocations.LastOrDefault(allocation => allocation.Address == address).Size;
        var identity = new ProducedCommandIdentity(
            scope.BuildCycle,
            scope.ThreadId,
            (nint)context,
            address,
            *(uint*)command,
            context->SortKey,
            context->ViewIndex,
            context->CurrentSubViewIndex,
            allocationSize,
            scope.PushedCommands.Count
        );
        scope.PushedCommands.Add(identity);
        producedCommands[address] = identity;
    }

    private void PreprocessCommandsDetour(
        ImmediateContext* immediateContext,
        RenderCommandBufferGroup* renderCommands,
        uint renderCommandCount
    )
    {
        try
        {
            if (Interlocked.CompareExchange(ref targetModel, 0, 0) != 0 && renderCommands != null && renderCommandCount <= 1_000_000)
            {
                for (var index = 0u; index < renderCommandCount; index++)
                {
                    var group = (byte*)renderCommands + index * 0x10;
                    var command = *(nint*)(group + 8);
                    if (!producedCommands.TryGetValue(command, out var producer))
                        continue;
                    if (*(uint*)group != producer.SortKey || *(uint*)command != producer.CommandType)
                    {
                        producedCommands.TryRemove(command, out _);
                        continue;
                    }

                    var snapshot = new CommandConsumptionSnapshot(
                        producer,
                        Stopwatch.GetTimestamp(),
                        Environment.CurrentManagedThreadId,
                        (nint)immediateContext,
                        (nint)renderCommands,
                        renderCommandCount,
                        index,
                        *(uint*)group,
                        ReadCommandArguments((void*)command, producer.CommandType),
                        HashRange((void*)command, producer.AllocationSize),
                        FormatCommandQwords((void*)command, producer.AllocationSize)
                    );
                    lock (stateLock)
                    {
                        if (
                            Interlocked.CompareExchange(ref targetModel, 0, 0) != 0
                            && consumptionEvents.Count < MaxConsumptionEvents
                            && !consumptionEvents.Any(item =>
                                item.Producer.Command == command
                                && item.RenderCommandBuffer == (nint)renderCommands
                                && item.GroupIndex == index
                            )
                        )
                        {
                            consumptionEvents.Add(snapshot);
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            DebugFileLog.Error("MaterialSubmissionProbe", exception, "Failed to observe command consumption");
        }
        preprocessCommandsHook.Original(immediateContext, renderCommands, renderCommandCount);
    }

    private nint PrepareDrawStateDetour(ImmediateContext* immediateContext, byte* state, int flags)
    {
        var command = state == null ? 0 : (nint)(state - 0x20);
        var observe = producedCommands.TryGetValue(command, out var producer) && producer.CommandType == 6;
        var result = prepareDrawStateHook.Original(immediateContext, state, flags);
        if (!observe || immediateContext == null || state == null)
            return result;

        try
        {
            var targets = new nint[5];
            for (var index = 0; index < targets.Length; index++)
                targets[index] = *(nint*)((byte*)immediateContext + 0x28 + index * 8);
            var snapshot = new CommandExecutionSnapshot(
                producer!,
                Stopwatch.GetTimestamp(),
                Environment.CurrentManagedThreadId,
                (nint)immediateContext,
                (nint)state,
                ReadCommandArguments((void*)command, producer!.CommandType),
                targets,
                (nint)immediateContext->CurrentDepthStencilBuffer,
                (nint)immediateContext->CurrentVertexShader,
                (nint)immediateContext->CurrentPixelShader,
                *(uint*)((byte*)immediateContext + 0xAC),
                *(ulong*)((byte*)immediateContext + 0xB0),
                *(uint*)((byte*)immediateContext + 0x17B4),
                *(ulong*)state,
                *(uint*)(state + 0x1C),
                immediateContext->CurrentPrimitiveTopology,
                *(int*)((byte*)immediateContext + 0x08),
                *(int*)((byte*)immediateContext + 0x0C),
                *(int*)((byte*)immediateContext + 0x10),
                *(int*)((byte*)immediateContext + 0x14),
                *(nint*)(state + 0x30)
            );
            lock (stateLock)
            {
                if (Interlocked.CompareExchange(ref targetModel, 0, 0) != 0 && executionEvents.Count < MaxExecutionEvents)
                    executionEvents.Add(snapshot);
            }
            producedCommands.TryRemove(command, out _);
        }
        catch (Exception exception)
        {
            DebugFileLog.Error("MaterialSubmissionProbe", exception, "Failed to observe command execution");
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
            scope.MaterialIndex,
            GetShpkName(scope.Material),
            (nint)scope.Context,
            scope.Before,
            after,
            CaptureGeometryState(scope.Context),
            commandDelta,
            payloadDelta,
            allocations,
            scope.PushedCommands.ToArray(),
            scope.MaterialObservation,
            scope.MaterialCallCount,
            scope.BuilderResult,
            scope.DuplicateApplied,
            scope.DuplicateStartPushIndex,
            scope.DuplicateBoundary,
            scope.DuplicateBuilderResult
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

    private static string GetShpkName(Material* material)
    {
        var resource = material == null ? null : material->MaterialResourceHandle;
        return resource == null ? "" : resource->ShpkName.ToString();
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

    private static GeometryStateSnapshot CaptureGeometryState(Context* context)
    {
        if (context == null)
            return default;

        var bytes = (byte*)context;
        var indexBuffer = *(nint*)(bytes + 0x888);
        var vertexDeclaration = *(nint*)(bytes + 0x890);
        var streams = new ulong[8];
        for (var index = 0; index < streams.Length; index++)
            streams[index] = *(ulong*)(bytes + 0x8C0 + index * 8);

        return new GeometryStateSnapshot(
            indexBuffer,
            FormatNativeObject(indexBuffer, 14),
            vertexDeclaration,
            FormatNativeObject(vertexDeclaration, 8),
            streams
        );
    }

    private static string FormatNativeObject(nint address, int qwordCount)
    {
        if (address == 0)
            return "none";
        return string.Join(',', Enumerable.Range(0, qwordCount).Select(index => $"+0x{index * 8:X}=0x{((ulong*)address)[index]:X}"));
    }

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

    private static CommandArguments ReadCommandArguments(void* command, uint commandType)
    {
        if (command == null)
            return default;
        var bytes = (byte*)command;
        return commandType switch
        {
            5 => new CommandArguments(*(uint*)(bytes + 0x0C), *(uint*)(bytes + 0x08), 0, 0),
            6 => new CommandArguments(*(uint*)(bytes + 0x18), *(uint*)(bytes + 0x14), *(int*)(bytes + 0x08), 0),
            7 => new CommandArguments(*(uint*)(bytes + 0x18), *(uint*)(bytes + 0x14), 0, *(uint*)(bytes + 0x1C)),
            _ => default,
        };
    }

    private static string FormatCommandQwords(void* command, ulong size)
    {
        if (command == null || size == 0)
            return "none";
        var qwordCount = checked((int)Math.Min(size / 8, 22));
        return string.Join(',', Enumerable.Range(0, qwordCount).Select(index => $"+0x{index * 8:X}=0x{((ulong*)command)[index]:X}"));
    }

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
        uint materialIndex,
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
        public uint MaterialIndex { get; } = materialIndex;
        public Context* Context { get; } = context;
        public ContextSnapshot Before { get; } = before;
        public ulong GeometryHash { get; } = geometryHash;
        public List<PendingAllocation> Allocations { get; } = [];
        public List<ProducedCommandIdentity> PushedCommands { get; } = [];
        public MaterialObservation? MaterialObservation { get; set; }
        public int MaterialCallCount { get; set; }
        public nint BuilderResult { get; set; }
        public bool DuplicateApplied { get; set; }
        public int DuplicateStartPushIndex { get; set; }
        public ContextSnapshot DuplicateBoundary { get; set; }
        public nint DuplicateBuilderResult { get; set; }
    }

    private readonly record struct PendingAllocation(nint Address, ulong Size);
}

internal sealed record MaterialSubmissionProbeCapture(
    IReadOnlyList<MaterialSubmissionProbeEvent> Builds,
    IReadOnlyList<CommandConsumptionSnapshot> Consumptions,
    IReadOnlyList<CommandExecutionSnapshot> Executions
);

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
    uint MaterialIndex,
    string ShpkName,
    nint Context,
    ContextSnapshot Before,
    ContextSnapshot After,
    GeometryStateSnapshot GeometryState,
    ArenaDelta CommandDelta,
    ArenaDelta PayloadDelta,
    IReadOnlyList<CommandAllocationSnapshot> CommandAllocations,
    IReadOnlyList<ProducedCommandIdentity> PushedCommands,
    MaterialObservation? MaterialObservation,
    int MaterialCallCount,
    nint BuilderResult,
    bool DuplicateApplied,
    int DuplicateStartPushIndex,
    ContextSnapshot DuplicateBoundary,
    nint DuplicateBuilderResult
);

internal sealed record ProducedCommandIdentity(
    long BuildCycle,
    int ProducerThreadId,
    nint Context,
    nint Command,
    uint CommandType,
    uint SortKey,
    int ViewIndex,
    byte SubViewIndex,
    ulong AllocationSize,
    int PushIndex
);

internal sealed record CommandConsumptionSnapshot(
    ProducedCommandIdentity Producer,
    long Timestamp,
    int ConsumerThreadId,
    nint ImmediateContext,
    nint RenderCommandBuffer,
    uint RenderCommandCount,
    uint GroupIndex,
    uint GroupSortKey,
    CommandArguments Arguments,
    ulong? PayloadHash,
    string PayloadQwords
);

internal readonly record struct CommandArguments(uint Count, uint StartIndex, int BaseVertex, uint InstanceCount);

internal sealed record CommandExecutionSnapshot(
    ProducedCommandIdentity Producer,
    long Timestamp,
    int ThreadId,
    nint ImmediateContext,
    nint State,
    CommandArguments Arguments,
    IReadOnlyList<nint> RenderTargets,
    nint DepthStencil,
    nint VertexShader,
    nint PixelShader,
    uint CurrentDepthState,
    ulong CurrentStencilState,
    uint CurrentBlendState,
    ulong CommandDepthStencilState,
    uint CommandRasterizerState,
    int PrimitiveTopology,
    int ScissorLeft,
    int ScissorTop,
    int ScissorRight,
    int ScissorBottom,
    nint IndexBuffer
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

internal readonly record struct GeometryStateSnapshot(
    nint IndexBuffer,
    string? IndexBufferQwords,
    nint VertexDeclaration,
    string? VertexDeclarationQwords,
    IReadOnlyList<ulong>? VertexStreams
);
#endif
