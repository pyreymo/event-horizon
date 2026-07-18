#if DEBUG
using System.Diagnostics;
using System.Threading;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Underpaint;
using Underpaint.Internal;
using RenderMaterial = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Material;
using RenderModel = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model;

namespace EventHorizon.Integration.Debug;

internal sealed unsafe class TransparentDrawCorrelationTracer : IDisposable
{
    private const string LogSource = "TransparentDrawCorrelation";
    private const int DonorSlot = 1;
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(15);
    private readonly ITargetManager targetManager;
    private readonly UnderpaintRenderer? underpaint;
    private readonly MaterialSubmissionContainerProbe submissionProbe;
    private readonly Lock stateLock = new();
    private DonorSnapshot? donor;
    private NativeGeometry? standaloneGeometry;
    private long captureStarted;
    private string state = "Idle";
    private bool capturing;
    private bool standaloneCapture;
    private bool standaloneSubmissionSeen;
    private bool disposed;

    public TransparentDrawCorrelationTracer(
        IGameInteropProvider gameInteropProvider,
        ITargetManager targetManager,
        UnderpaintRenderer? underpaint
    )
    {
        this.targetManager = targetManager;
        this.underpaint = underpaint;
        submissionProbe = new MaterialSubmissionContainerProbe(gameInteropProvider, underpaint);
    }

    public string State
    {
        get
        {
            lock (stateLock)
                return state;
        }
    }

    public string DonorSummary
    {
        get
        {
            lock (stateLock)
                return donor?.Summary ?? "No donor snapshot";
        }
    }

    public bool IsCapturing
    {
        get
        {
            lock (stateLock)
                return capturing;
        }
    }

    public void Arm()
    {
        Arm(false);
    }

    public void ArmNativeDuplicate()
    {
        Arm(true);
    }

    public void ArmCustomNativeGeometry()
    {
        if (underpaint == null)
        {
            SetState("Unavailable: Underpaint failed to initialize");
            return;
        }

        standaloneGeometry ??= underpaint.CreateNativeGeometry([new(-0.75f, 0f, 0f), new(0.75f, 0f, 0f), new(0f, 1.5f, 0f)], [0, 1, 2]);
        underpaint.ArmNativeGeometrySubmission(standaloneGeometry);
        lock (stateLock)
        {
            donor = null;
            standaloneCapture = true;
            standaloneSubmissionSeen = false;
            captureStarted = Stopwatch.GetTimestamp();
            state = "Waiting for an independent main-view native submission site";
            capturing = true;
        }
        underpaint.Diagnostics.BeginTransparentDrawCapture(128, 4);
        DebugFileLog.Information(LogSource, "Standalone native geometry armed; no character, equipment slot, or material filter is active");
    }

    private void Arm(bool duplicateMainSubmission, bool customGeometrySubmission = false)
    {
        if (underpaint == null)
        {
            SetState("Unavailable: Underpaint failed to initialize");
            return;
        }

        var snapshot = TryCaptureDonor();
        if (snapshot == null)
        {
            SetState("Arm failed: current target is not a loaded PC");
            return;
        }

        lock (stateLock)
        {
            donor = snapshot;
            standaloneCapture = false;
            standaloneSubmissionSeen = false;
            captureStarted = Stopwatch.GetTimestamp();
            state =
                customGeometrySubmission ? "Submitting one custom native triangle through the target material"
                : duplicateMainSubmission ? "Capturing one native duplicate (Slot 1 / Material 2 / charactertransparency)"
                : "Capturing material submission (Slot 1 / Material 2 / charactertransparency)";
            capturing = true;
        }
        submissionProbe.Arm(snapshot.Model.Model, duplicateMainSubmission, customGeometrySubmission);
        underpaint.Diagnostics.BeginTransparentDrawCapture(128, 4);
        LogDonor(snapshot);
        DebugFileLog.Information(
            LogSource,
            "Capture armed NativeDuplicate={NativeDuplicate} CustomGeometry={CustomGeometry}",
            duplicateMainSubmission,
            customGeometrySubmission
        );
    }

    public void Cancel(string reason = "manual-cancel")
    {
        lock (stateLock)
        {
            if (!capturing)
                return;
            capturing = false;
            state = $"Cancelled: {reason}";
        }
        underpaint?.Diagnostics.CancelTransparentDrawCapture(reason);
        underpaint?.CancelNativeGeometrySubmission();
        submissionProbe.Stop();
        DebugFileLog.Information(LogSource, "Capture cancelled: {Reason}", reason);
    }

    public void Update()
    {
        DonorSnapshot? currentDonor;
        bool currentStandaloneCapture;
        long started;
        lock (stateLock)
        {
            if (!capturing)
                return;
            currentDonor = donor;
            currentStandaloneCapture = standaloneCapture;
            started = captureStarted;
        }

        if (!currentStandaloneCapture && (currentDonor == null || !DonorStillValid(currentDonor)))
        {
            Cancel("donor-invalid");
            return;
        }
        if (currentStandaloneCapture && underpaint?.TryTakeNativeGeometrySubmission(out var standaloneSubmission) == true)
        {
            lock (stateLock)
                standaloneSubmissionSeen = true;
            LogStandaloneSubmission(standaloneSubmission);
            SetState(
                standaloneSubmission.Succeeded
                    ? "Independent native submission completed; collecting draw evidence"
                    : $"Independent native submission failed: {standaloneSubmission.Failure}"
            );
        }
        if (Stopwatch.GetElapsedTime(started) >= CaptureTimeout)
        {
            underpaint?.Diagnostics.CancelTransparentDrawCapture("timeout");
        }

        if (underpaint?.Diagnostics.TryTakeTransparentDrawCapture(out var capture) == true)
            Complete(capture);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Cancel("dispose");
        standaloneGeometry?.Dispose();
        submissionProbe.Dispose();
    }

    private void Complete(TransparentDrawCapture capture)
    {
        DonorSnapshot? currentDonor;
        var submissionCapture = submissionProbe.StopAndTake();
        var submissionEvents = submissionCapture.Builds;
        lock (stateLock)
        {
            currentDonor = donor;
            capturing = false;
            state =
                standaloneCapture && !standaloneSubmissionSeen
                    ? $"No compatible 20/24 native submission site was found ({capture.Reason})"
                    : $"Complete: {capture.Draws.Count} draws, {submissionEvents.Count} builds, {submissionCapture.Executions.Count} executed commands ({capture.Reason})";
        }

        foreach (var submissionEvent in submissionEvents)
            LogSubmission(submissionEvent);
        foreach (var consumption in submissionCapture.Consumptions)
            LogCommandConsumption(consumption);
        foreach (var execution in submissionCapture.Executions)
            LogCommandExecution(execution);

        foreach (var draw in capture.Draws)
            LogDraw(draw);

        if (currentDonor == null)
        {
            underpaint?.CancelNativeGeometrySubmission();
            DebugFileLog.Information(
                LogSource,
                "Standalone capture complete Reason={Reason} Draws={Draws} SubmissionSeen={SubmissionSeen}",
                capture.Reason,
                capture.Draws.Count,
                standaloneSubmissionSeen
            );
            return;
        }

        var match = TransparentDrawCorrelationMatcher.Match(
            new CorrelationDonorEvidence(true, currentDonor.TextureResources),
            capture.Draws.Select(ToEvidence).ToArray()
        );
        DebugFileLog.Information(
            LogSource,
            "Capture complete Reason={Reason} StartedAt={StartedAt} CompletedAt={CompletedAt} Draws={Draws} SubmissionBuilds={SubmissionBuilds} SubmissionCommandsConsumed={SubmissionCommandsConsumed} SubmissionCommandsExecuted={SubmissionCommandsExecuted} Match={Match} Unique={Unique} Candidates={Candidates}",
            capture.Reason,
            capture.StartedAt,
            capture.CompletedAt,
            capture.Draws.Count,
            submissionEvents.Count,
            submissionCapture.Consumptions.Count,
            submissionCapture.Executions.Count,
            match.Conclusion,
            match.IsUnique,
            match.Candidates.Count
        );
        foreach (var candidate in match.Candidates.Take(8))
        {
            DebugFileLog.Information(
                LogSource,
                "Pair StageA={StageA} StageC={StageC} Score={Score} Evidence={Evidence}",
                candidate.StageA.Sequence,
                candidate.StageC.Sequence,
                candidate.Score,
                string.Join(',', candidate.Evidence)
            );
        }
    }

    private static void LogStandaloneSubmission(NativeGeometryStandaloneSubmission item)
    {
        var submission = item.Submission;
        DebugFileLog.Information(
            LogSource,
            "StandaloneNativeSubmission Success={Success} Failure={Failure} ModelRenderer=0x{ModelRenderer:X} MaterialParams=0x{MaterialParams:X} Model=0x{Model:X} View={View} SubView={SubView} SourceVB=0x{SourceVB:X} SourceIB=0x{SourceIB:X} SourceVertexDeclaration=0x{SourceVertexDeclaration:X} SourceStrides={SourceStream0Stride}/{SourceStream1Stride} SourceRange={SourceVertices}/{SourceStart}/{SourceIndices} Context=0x{Context:X} VB=0x{VB:X} VBResource=0x{VBResource:X} IB=0x{IB:X} IBResource=0x{IBResource:X} VertexDeclaration=0x{VertexDeclaration:X} Range={Vertices}/0/{Indices} Result=0x{Result:X}",
            item.Succeeded,
            item.Failure ?? "",
            item.ModelRenderer,
            item.MaterialParameters,
            item.Model,
            item.View,
            item.SubView,
            item.SourceVertexBuffer,
            item.SourceIndexBuffer,
            item.SourceVertexDeclaration,
            item.SourceStream0Stride,
            item.SourceStream1Stride,
            item.SourceVertexCount,
            item.SourceStartIndex,
            item.SourceIndexCount,
            submission.Context,
            submission.VertexBuffer,
            submission.VertexBufferResource,
            submission.IndexBuffer,
            submission.IndexBufferResource,
            submission.VertexDeclaration,
            submission.VertexCount,
            submission.IndexCount,
            submission.BuilderResult
        );
    }

    private DonorSnapshot? TryCaptureDonor()
    {
        var target = targetManager.Target;
        if (target == null || target.Address == 0)
            return null;
        var gameObject = (GameObject*)target.Address;
        if (gameObject->ObjectKind != ObjectKind.Pc || gameObject->DrawObject == null)
            return null;

        var characterBase = (CharacterBase*)gameObject->DrawObject;
        if (characterBase->Models == null || characterBase->SlotCount <= DonorSlot || characterBase->SlotCount > 64)
            return null;

        var model = characterBase->Models[DonorSlot];
        if (model == null || model->MaterialCount is < 0 or > 64 || model->Materials == null)
            return null;

        var materials = new List<DonorMaterialSnapshot>();
        var textureResources = new HashSet<nint>();
        for (var materialIndex = 0; materialIndex < model->MaterialCount; materialIndex++)
        {
            var material = model->Materials[materialIndex];
            if (material == null || material->TextureCount > 64)
                continue;
            var textures = new List<nint>();
            foreach (var textureEntry in material->TexturesSpan)
            {
                var resource = textureEntry.Texture == null ? null : textureEntry.Texture->Texture;
                var pointer = resource == null ? 0 : (nint)resource->D3D11Texture2D;
                if (pointer == 0)
                    continue;
                textures.Add(pointer);
                textureResources.Add(pointer);
            }
            materials.Add(
                new DonorMaterialSnapshot(
                    (nint)material,
                    (nint)material->MaterialResourceHandle,
                    material->ShaderFlags,
                    material->ShaderKeyCount is >= 0 and <= 64 ? material->ShaderKeyValuesSpan.ToArray() : [],
                    (nint)material->MaterialParameterCBuffer,
                    textures.ToArray(),
                    GetShpkName(material),
                    material->MaterialResourceHandle == null ? 0 : (nint)material->MaterialResourceHandle->ShaderPackageResourceHandle
                )
            );
        }

        return new DonorSnapshot(
            target.Address,
            gameObject->EntityId,
            gameObject->ObjectIndex,
            (nint)characterBase,
            target.Name.TextValue,
            new DonorModelSnapshot((nint)model, DonorSlot, (nint)model->ModelResourceHandle, materials.ToArray()),
            textureResources
        );
    }

    private bool DonorStillValid(DonorSnapshot snapshot)
    {
        var target = targetManager.Target;
        if (target == null || target.Address != snapshot.GameObject)
            return false;
        var gameObject = (GameObject*)target.Address;
        return gameObject->EntityId == snapshot.EntityId && (nint)gameObject->DrawObject == snapshot.CharacterBase;
    }

    private static string GetShpkName(RenderMaterial* material)
    {
        var resource = material == null ? null : material->MaterialResourceHandle;
        return resource == null ? "" : resource->ShpkName.ToString();
    }

    private static CorrelationDrawEvidence ToEvidence(TransparentNativeDrawSnapshot draw) =>
        new(
            draw.Sequence,
            draw.Stage == TransparentDrawStage.StageA ? CorrelationDrawStage.StageA : CorrelationDrawStage.StageC,
            draw.VertexBuffers.FirstOrDefault().Buffer,
            draw.IndexBuffer?.Buffer ?? 0,
            draw.ElementCount,
            draw.StartIndex,
            draw.BaseVertex,
            draw.PixelShaderResources.Select(resource => resource.Resource).Where(pointer => pointer != 0).ToHashSet(),
            draw.VertexConstantBuffers.Concat(draw.PixelConstantBuffers)
                .Where(buffer => buffer.ContentHash.HasValue)
                .Select(buffer => buffer.ContentHash!.Value)
                .ToHashSet()
        );

    private static void LogDonor(DonorSnapshot snapshot)
    {
        DebugFileLog.Information(
            LogSource,
            "Donor Name={Name} ObjectIndex={ObjectIndex} GameObject=0x{GameObject:X} EntityId=0x{EntityId:X} CharacterBase=0x{CharacterBase:X} Slot={Slot} Model=0x{Model:X} TextureResources={Textures}",
            snapshot.Name,
            snapshot.ObjectIndex,
            snapshot.GameObject.ToInt64(),
            snapshot.EntityId,
            snapshot.CharacterBase.ToInt64(),
            snapshot.Model.Slot,
            snapshot.Model.Model.ToInt64(),
            snapshot.TextureResources.Count
        );
        var model = snapshot.Model;
        foreach (var material in model.Materials)
        {
            DebugFileLog.Debug(
                LogSource,
                "DonorMaterial Model=0x{Model:X} Slot={Slot} ModelResource=0x{ModelResource:X} Material=0x{Material:X} MaterialResource=0x{MaterialResource:X} Shpk={Shpk} ShaderPackage=0x{ShaderPackage:X} ShaderFlags=0x{ShaderFlags:X} ShaderKeys={ShaderKeys} MaterialCBuffer=0x{MaterialCBuffer:X} Textures={Textures}",
                model.Model.ToInt64(),
                model.Slot,
                model.ModelResource.ToInt64(),
                material.Material.ToInt64(),
                material.MaterialResource.ToInt64(),
                material.ShpkName,
                material.ShaderPackage.ToInt64(),
                material.ShaderFlags,
                string.Join(',', material.ShaderKeys.Select(value => $"0x{value:X}")),
                material.MaterialCBuffer.ToInt64(),
                string.Join(',', material.TextureResources.Select(value => $"0x{value:X}"))
            );
        }
    }

    private static void LogDraw(TransparentNativeDrawSnapshot draw)
    {
        DebugFileLog.Debug(
            LogSource,
            "Draw Sequence={Sequence} Timestamp={Timestamp} Stage={Stage} Thread={Thread} Type={Type} Count={Count} Instances={Instances} StartIndex={StartIndex} BaseVertex={BaseVertex} StartVertex={StartVertex} StartInstance={StartInstance} VS=0x{VS:X} PS=0x{PS:X} Layout=0x{Layout:X} VB={VB} IB={IB} VSConstants={VSConstants} PSConstants={PSConstants} Resources={Resources} Stack={Stack}",
            draw.Sequence,
            draw.Timestamp,
            draw.Stage,
            draw.ThreadId,
            draw.DrawType,
            draw.ElementCount,
            draw.InstanceCount,
            draw.StartIndex,
            draw.BaseVertex,
            draw.StartVertex,
            draw.StartInstance,
            draw.VertexShader.ToInt64(),
            draw.PixelShader.ToInt64(),
            draw.InputLayout.ToInt64(),
            string.Join(',', draw.VertexBuffers.Select(buffer => $"{buffer.Slot}:0x{buffer.Buffer:X}/{buffer.Stride}/{buffer.Offset}")),
            draw.IndexBuffer is { } index ? $"0x{index.Buffer:X}/{index.Format}/{index.Offset}" : "none",
            FormatConstants(draw.VertexConstantBuffers),
            FormatConstants(draw.PixelConstantBuffers),
            string.Join(',', draw.PixelShaderResources.Select(resource => $"{resource.Slot}:0x{resource.Resource:X}")),
            string.Join('>', draw.NativeStack)
        );
    }

    private static void LogSubmission(MaterialSubmissionProbeEvent item)
    {
        DebugFileLog.Debug(
            LogSource,
            "SubmissionBuild Cycle={Cycle} Timestamp={Timestamp} Caller={Caller} Thread={Thread} ModelRenderer=0x{ModelRenderer:X} ModelParams=0x{ModelParams:X} Model=0x{Model:X} ModelResource=0x{ModelResource:X} GeometryIndex={GeometryIndex} GeometryEntry=0x{GeometryEntry:X} GeometryHash={GeometryHash:X16} Material=0x{Material:X} MaterialIndex={MaterialIndex} MaterialResource=0x{MaterialResource:X} Shpk={Shpk} Context=0x{Context:X} View={ViewBefore}->{ViewAfter} SubView={SubViewBefore}->{SubViewAfter} SortKey=0x{SortKeyBefore:X}->0x{SortKeyAfter:X}",
            item.BuildCycle,
            item.Timestamp,
            item.NativeCaller,
            item.ThreadId,
            item.ModelRenderer.ToInt64(),
            item.ModelParams.ToInt64(),
            item.Model.ToInt64(),
            item.ModelResource.ToInt64(),
            item.GeometryIndex,
            item.GeometryEntry.ToInt64(),
            item.GeometryHash,
            item.Material.ToInt64(),
            item.MaterialIndex,
            item.MaterialResource.ToInt64(),
            item.ShpkName,
            item.Context.ToInt64(),
            item.Before.ViewIndex,
            item.After.ViewIndex,
            item.Before.SubViewIndex,
            item.After.SubViewIndex,
            item.Before.SortKey,
            item.After.SortKey
        );
        DebugFileLog.Debug(
            LogSource,
            "SubmissionArena Cycle={Cycle} CommandBase=0x{CommandBaseBefore:X}->0x{CommandBaseAfter:X} CommandUsed={CommandUsedBefore}->{CommandUsedAfter} NewCommands=0x{CommandAddress:X}/{CommandSize}/0x{CommandHash:X16}/{CommandStatus} PayloadBase=0x{PayloadBaseBefore:X}->0x{PayloadBaseAfter:X} PayloadUsed={PayloadUsedBefore}->{PayloadUsedAfter} NewPayload=0x{PayloadAddress:X}/{PayloadSize}/0x{PayloadHash:X16}/{PayloadStatus} Allocations={Allocations}",
            item.BuildCycle,
            item.Before.CommandAllocationBase.ToInt64(),
            item.After.CommandAllocationBase.ToInt64(),
            item.Before.CommandAllocationUsedSize,
            item.After.CommandAllocationUsedSize,
            item.CommandDelta.Address.ToInt64(),
            item.CommandDelta.Size,
            item.CommandDelta.Hash,
            item.CommandDelta.Status,
            item.Before.AllocationBase,
            item.After.AllocationBase,
            item.Before.AllocationUsedSize,
            item.After.AllocationUsedSize,
            item.PayloadDelta.Address.ToInt64(),
            item.PayloadDelta.Size,
            item.PayloadDelta.Hash,
            item.PayloadDelta.Status,
            string.Join(
                ',',
                item.CommandAllocations.Select(allocation => $"0x{allocation.Address:X}/{allocation.Size}/0x{allocation.Hash:X16}")
            )
        );
        DebugFileLog.Debug(
            LogSource,
            "SubmissionGeometry Cycle={Cycle} IndexBuffer=0x{IndexBuffer:X} VertexDeclaration=0x{VertexDeclaration:X} Streams={Streams} IndexBufferObject={IndexBufferObject} VertexDeclarationObject={VertexDeclarationObject}",
            item.BuildCycle,
            item.GeometryState.IndexBuffer.ToInt64(),
            item.GeometryState.VertexDeclaration.ToInt64(),
            item.GeometryState.VertexStreams == null
                ? "none"
                : string.Join(',', item.GeometryState.VertexStreams.Select((value, index) => $"+0x{index * 8:X}=0x{value:X}")),
            item.GeometryState.IndexBufferQwords ?? "none",
            item.GeometryState.VertexDeclarationQwords ?? "none"
        );
        var material = item.MaterialObservation;
        DebugFileLog.Debug(
            LogSource,
            "SubmissionMaterial Cycle={Cycle} Observed={Observed} Params=0x{Params:X} Return=0x{Return:X} BeforeHash=0x{BeforeHash:X16} AfterHash=0x{AfterHash:X16} Changed={Changed} Before={Before} After={After}",
            item.BuildCycle,
            material != null,
            material?.Params.ToInt64() ?? 0,
            material?.ReturnValue.ToInt64() ?? 0,
            material?.BeforeHash ?? 0,
            material?.AfterHash ?? 0,
            material?.ChangedOffsets ?? "none",
            material?.BeforeQwords ?? "none",
            material?.AfterQwords ?? "none"
        );
        DebugFileLog.Debug(
            LogSource,
            "SubmissionCommands Cycle={Cycle} Pushed={Pushed}",
            item.BuildCycle,
            string.Join(
                ',',
                item.PushedCommands.Select(command =>
                    $"#{command.PushIndex}:0x{command.Command:X}/type{command.CommandType}/sort0x{command.SortKey:X}/view{command.ViewIndex}.{command.SubViewIndex}/size{command.AllocationSize}"
                )
            )
        );
        DebugFileLog.Debug(
            LogSource,
            "SubmissionDuplicate Cycle={Cycle} Applied={Applied} OriginalPushCount={OriginalPushCount} BuilderReturn=0x{BuilderReturn:X} DuplicateReturn=0x{DuplicateReturn:X} BoundaryView={BoundaryView}.{BoundarySubView} BoundarySort=0x{BoundarySort:X} BoundaryCommandUsed={BoundaryCommandUsed} MaterialCalls={MaterialCalls}",
            item.BuildCycle,
            item.DuplicateApplied,
            item.DuplicateApplied ? item.DuplicateStartPushIndex : item.PushedCommands.Count,
            item.BuilderResult.ToInt64(),
            item.DuplicateBuilderResult.ToInt64(),
            item.DuplicateBoundary.ViewIndex,
            item.DuplicateBoundary.SubViewIndex,
            item.DuplicateBoundary.SortKey,
            item.DuplicateBoundary.CommandAllocationUsedSize,
            item.MaterialCallCount
        );
    }

    private static void LogCommandConsumption(CommandConsumptionSnapshot item)
    {
        DebugFileLog.Debug(
            LogSource,
            "SubmissionConsumer Cycle={Cycle} PushIndex={PushIndex} Timestamp={Timestamp} ProducerThread={ProducerThread} ConsumerThread={ConsumerThread} Context=0x{Context:X} ImmediateContext=0x{ImmediateContext:X} Command=0x{Command:X} Type={Type} ProducerSort=0x{ProducerSort:X} GroupSort=0x{GroupSort:X} View={View}.{SubView} Buffer=0x{Buffer:X} BufferCount={BufferCount} GroupIndex={GroupIndex} Count={Count} StartIndex={StartIndex} BaseVertex={BaseVertex} Instances={Instances} PayloadHash=0x{PayloadHash:X16} Payload={Payload}",
            item.Producer.BuildCycle,
            item.Producer.PushIndex,
            item.Timestamp,
            item.Producer.ProducerThreadId,
            item.ConsumerThreadId,
            item.Producer.Context.ToInt64(),
            item.ImmediateContext.ToInt64(),
            item.Producer.Command.ToInt64(),
            item.Producer.CommandType,
            item.Producer.SortKey,
            item.GroupSortKey,
            item.Producer.ViewIndex,
            item.Producer.SubViewIndex,
            item.RenderCommandBuffer.ToInt64(),
            item.RenderCommandCount,
            item.GroupIndex,
            item.Arguments.Count,
            item.Arguments.StartIndex,
            item.Arguments.BaseVertex,
            item.Arguments.InstanceCount,
            item.PayloadHash,
            item.PayloadQwords
        );
    }

    private static void LogCommandExecution(CommandExecutionSnapshot item)
    {
        DebugFileLog.Debug(
            LogSource,
            "SubmissionExecutor Cycle={Cycle} PushIndex={PushIndex} Timestamp={Timestamp} Thread={Thread} Command=0x{Command:X} Type={Type} Sort=0x{Sort:X} View={View}.{SubView} ImmediateContext=0x{ImmediateContext:X} State=0x{State:X} Count={Count} StartIndex={StartIndex} BaseVertex={BaseVertex} Instances={Instances} RT={RenderTargets} DepthStencil=0x{DepthStencil:X} VS=0x{VS:X} PS=0x{PS:X} DepthState=0x{DepthState:X} StencilState=0x{StencilState:X} BlendState=0x{BlendState:X} CommandDepthStencil=0x{CommandDepthStencil:X} Rasterizer=0x{Rasterizer:X} Topology={Topology} Scissor={ScissorLeft},{ScissorTop},{ScissorRight},{ScissorBottom} IndexBuffer=0x{IndexBuffer:X}",
            item.Producer.BuildCycle,
            item.Producer.PushIndex,
            item.Timestamp,
            item.ThreadId,
            item.Producer.Command.ToInt64(),
            item.Producer.CommandType,
            item.Producer.SortKey,
            item.Producer.ViewIndex,
            item.Producer.SubViewIndex,
            item.ImmediateContext.ToInt64(),
            item.State.ToInt64(),
            item.Arguments.Count,
            item.Arguments.StartIndex,
            item.Arguments.BaseVertex,
            item.Arguments.InstanceCount,
            string.Join(',', item.RenderTargets.Select((target, index) => $"{index}:0x{target:X}")),
            item.DepthStencil.ToInt64(),
            item.VertexShader.ToInt64(),
            item.PixelShader.ToInt64(),
            item.CurrentDepthState,
            item.CurrentStencilState,
            item.CurrentBlendState,
            item.CommandDepthStencilState,
            item.CommandRasterizerState,
            item.PrimitiveTopology,
            item.ScissorLeft,
            item.ScissorTop,
            item.ScissorRight,
            item.ScissorBottom,
            item.IndexBuffer.ToInt64()
        );
    }

    private static string FormatConstants(IReadOnlyList<TransparentConstantBufferSnapshot> buffers) =>
        string.Join(
            ',',
            buffers.Select(buffer =>
                $"{buffer.Slot}:0x{buffer.Buffer:X}/{buffer.ByteWidth}/{(buffer.ContentHash is { } hash ? hash.ToString("X16") : "unhashed")}"
            )
        );

    private void SetState(string value)
    {
        lock (stateLock)
            state = value;
    }

    private sealed record DonorSnapshot(
        nint GameObject,
        uint EntityId,
        int ObjectIndex,
        nint CharacterBase,
        string Name,
        DonorModelSnapshot Model,
        IReadOnlySet<nint> TextureResources
    )
    {
        public string Summary =>
            $"{Name} | object #{ObjectIndex} | slot {Model.Slot} | {Model.Materials.Count} materials | {TextureResources.Count} textures";
    }

    private sealed record DonorModelSnapshot(nint Model, int Slot, nint ModelResource, IReadOnlyList<DonorMaterialSnapshot> Materials);

    private sealed record DonorMaterialSnapshot(
        nint Material,
        nint MaterialResource,
        uint ShaderFlags,
        IReadOnlyList<uint> ShaderKeys,
        nint MaterialCBuffer,
        IReadOnlyList<nint> TextureResources,
        string ShpkName,
        nint ShaderPackage
    );
}
#endif
