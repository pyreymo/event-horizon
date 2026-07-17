#if DEBUG
using System.Diagnostics;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Underpaint;
using RenderMaterial = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Material;
using RenderModel = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model;

namespace EventHorizon.Integration.Debug;

internal sealed unsafe class TransparentDrawCorrelationTracer : IDisposable
{
    private const string LogSource = "TransparentDrawCorrelation";
    private const int MaxCallbackEvents = 1024;
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(15);
    private readonly ITargetManager targetManager;
    private readonly UnderpaintRenderer? underpaint;
    private readonly Hook<OnRenderMaterialDelegate> onRenderMaterialHook;
    private readonly Lock stateLock = new();
    private readonly List<RenderCallbackEvent> callbackEvents = [];
    private DonorSnapshot? donor;
    private long captureStarted;
    private string state = "Idle";
    private bool capturing;
    private bool disposed;

    private delegate void OnRenderMaterialDelegate(CharacterBase* characterBase, ModelRenderer.OnRenderMaterialParams* param);

    public TransparentDrawCorrelationTracer(
        IGameInteropProvider gameInteropProvider,
        ITargetManager targetManager,
        UnderpaintRenderer? underpaint
    )
    {
        this.targetManager = targetManager;
        this.underpaint = underpaint;
        onRenderMaterialHook = gameInteropProvider.HookFromAddress<OnRenderMaterialDelegate>(
            (nint)CharacterBase.StaticVirtualTablePointer->OnRenderMaterial,
            OnRenderMaterialDetour
        );
        onRenderMaterialHook.Enable();
        PlayerAdmissionDebugTrace.RenderModelObserved += OnRenderModelObserved;
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
            callbackEvents.Clear();
            captureStarted = Stopwatch.GetTimestamp();
            state = "Capturing (bounded to 4 Stage A frames / 128 draws / 15s)";
            capturing = true;
        }
        underpaint.Diagnostics.BeginTransparentDrawCapture(128, 4);
        LogDonor(snapshot);
        DebugFileLog.Information(LogSource, "Capture armed");
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
        DebugFileLog.Information(LogSource, "Capture cancelled: {Reason}", reason);
    }

    public void Update()
    {
        DonorSnapshot? currentDonor;
        long started;
        lock (stateLock)
        {
            if (!capturing)
                return;
            currentDonor = donor;
            started = captureStarted;
        }

        if (currentDonor == null || !DonorStillValid(currentDonor))
        {
            Cancel("donor-invalid");
            return;
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
        PlayerAdmissionDebugTrace.RenderModelObserved -= OnRenderModelObserved;
        onRenderMaterialHook.Dispose();
    }

    private void Complete(TransparentDrawCapture capture)
    {
        DonorSnapshot? currentDonor;
        RenderCallbackEvent[] events;
        lock (stateLock)
        {
            currentDonor = donor;
            events = callbackEvents.ToArray();
            capturing = false;
            state = $"Complete: {capture.Draws.Count} draws, {events.Length} callbacks ({capture.Reason})";
        }

        if (currentDonor == null)
            return;

        foreach (var callbackEvent in events)
        {
            DebugFileLog.Debug(
                LogSource,
                "Callback Kind={Kind} Timestamp={Timestamp} Thread={Thread} Model=0x{Model:X} Slot={Slot} Material=0x{Material:X} MaterialIndex={MaterialIndex} MaterialResource=0x{MaterialResource:X}",
                callbackEvent.Kind,
                callbackEvent.Timestamp,
                callbackEvent.ThreadId,
                callbackEvent.Model.ToInt64(),
                callbackEvent.Slot,
                callbackEvent.Material.ToInt64(),
                callbackEvent.MaterialIndex,
                callbackEvent.MaterialResource.ToInt64()
            );
        }

        foreach (var draw in capture.Draws)
            LogDraw(draw);

        var match = TransparentDrawCorrelationMatcher.Match(
            new CorrelationDonorEvidence(true, currentDonor.TextureResources),
            capture.Draws.Select(ToEvidence).ToArray()
        );
        DebugFileLog.Information(
            LogSource,
            "Capture complete Reason={Reason} StartedAt={StartedAt} CompletedAt={CompletedAt} Draws={Draws} Callbacks={Callbacks} Match={Match} Unique={Unique} Candidates={Candidates}",
            capture.Reason,
            capture.StartedAt,
            capture.CompletedAt,
            capture.Draws.Count,
            events.Length,
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

    private void OnRenderModelObserved(nint characterBaseAddress, nint modelAddress)
    {
        try
        {
            var characterBase = (CharacterBase*)characterBaseAddress;
            var model = (RenderModel*)modelAddress;
            if (IsCurrentDonor(characterBase))
                AddCallback("OnRenderModel", characterBase, model, null, -1);
        }
        catch (Exception exception)
        {
            DebugFileLog.Error(LogSource, exception, "OnRenderModel observation failed");
        }
    }

    private void OnRenderMaterialDetour(CharacterBase* characterBase, ModelRenderer.OnRenderMaterialParams* param)
    {
        try
        {
            if (IsCurrentDonor(characterBase) && param != null)
            {
                var model = param->Model;
                var materialIndex = checked((int)param->MaterialIndex);
                var material =
                    model != null && materialIndex >= 0 && materialIndex < model->MaterialCount ? model->Materials[materialIndex] : null;
                AddCallback("OnRenderMaterial", characterBase, model, material, materialIndex);
            }
        }
        catch (Exception exception)
        {
            DebugFileLog.Error(LogSource, exception, "OnRenderMaterial observation failed");
        }
        finally
        {
            onRenderMaterialHook.Original(characterBase, param);
        }
    }

    private bool IsCurrentDonor(CharacterBase* characterBase)
    {
        lock (stateLock)
            return capturing && donor?.CharacterBase == (nint)characterBase;
    }

    private void AddCallback(string kind, CharacterBase* characterBase, RenderModel* model, RenderMaterial* material, int materialIndex)
    {
        lock (stateLock)
        {
            if (!capturing || donor?.CharacterBase != (nint)characterBase)
                return;
            if (callbackEvents.Count >= MaxCallbackEvents)
            {
                capturing = false;
                state = "Cancelled: callback-event-limit";
                underpaint?.Diagnostics.CancelTransparentDrawCapture("callback-event-limit");
                return;
            }

            callbackEvents.Add(
                new RenderCallbackEvent(
                    kind,
                    Stopwatch.GetTimestamp(),
                    Environment.CurrentManagedThreadId,
                    (nint)model,
                    FindModelSlot(characterBase, model),
                    (nint)material,
                    materialIndex,
                    material == null ? 0 : (nint)material->MaterialResourceHandle
                )
            );
        }
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
        if (characterBase->Models == null || characterBase->SlotCount is <= 0 or > 64)
            return null;

        var models = new List<DonorModelSnapshot>();
        var textureResources = new HashSet<nint>();
        for (var slot = 0; slot < characterBase->SlotCount; slot++)
        {
            var model = characterBase->Models[slot];
            if (model == null || model->MaterialCount is < 0 or > 64 || model->Materials == null)
                continue;

            var materials = new List<DonorMaterialSnapshot>();
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
                        textures.ToArray()
                    )
                );
            }
            models.Add(new DonorModelSnapshot((nint)model, slot, (nint)model->ModelResourceHandle, materials.ToArray()));
        }

        return new DonorSnapshot(
            target.Address,
            gameObject->EntityId,
            gameObject->ObjectIndex,
            (nint)characterBase,
            target.Name.TextValue,
            models.ToArray(),
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

    private static int FindModelSlot(CharacterBase* characterBase, RenderModel* model)
    {
        if (characterBase == null || model == null || characterBase->Models == null)
            return -1;
        for (var slot = 0; slot < characterBase->SlotCount; slot++)
        {
            if (characterBase->Models[slot] == model)
                return slot;
        }
        return -1;
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
            "Donor Name={Name} ObjectIndex={ObjectIndex} GameObject=0x{GameObject:X} EntityId=0x{EntityId:X} CharacterBase=0x{CharacterBase:X} Models={Models} TextureResources={Textures}",
            snapshot.Name,
            snapshot.ObjectIndex,
            snapshot.GameObject.ToInt64(),
            snapshot.EntityId,
            snapshot.CharacterBase.ToInt64(),
            snapshot.Models.Count,
            snapshot.TextureResources.Count
        );
        foreach (var model in snapshot.Models)
        {
            foreach (var material in model.Materials)
            {
                DebugFileLog.Debug(
                    LogSource,
                    "DonorMaterial Model=0x{Model:X} Slot={Slot} ModelResource=0x{ModelResource:X} Material=0x{Material:X} MaterialResource=0x{MaterialResource:X} ShaderFlags=0x{ShaderFlags:X} ShaderKeys={ShaderKeys} MaterialCBuffer=0x{MaterialCBuffer:X} Textures={Textures}",
                    model.Model.ToInt64(),
                    model.Slot,
                    model.ModelResource.ToInt64(),
                    material.Material.ToInt64(),
                    material.MaterialResource.ToInt64(),
                    material.ShaderFlags,
                    string.Join(',', material.ShaderKeys.Select(value => $"0x{value:X}")),
                    material.MaterialCBuffer.ToInt64(),
                    string.Join(',', material.TextureResources.Select(value => $"0x{value:X}"))
                );
            }
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
        IReadOnlyList<DonorModelSnapshot> Models,
        IReadOnlySet<nint> TextureResources
    )
    {
        public string Summary => $"{Name} | object #{ObjectIndex} | {Models.Count} models | {TextureResources.Count} textures";
    }

    private sealed record DonorModelSnapshot(nint Model, int Slot, nint ModelResource, IReadOnlyList<DonorMaterialSnapshot> Materials);

    private sealed record DonorMaterialSnapshot(
        nint Material,
        nint MaterialResource,
        uint ShaderFlags,
        IReadOnlyList<uint> ShaderKeys,
        nint MaterialCBuffer,
        IReadOnlyList<nint> TextureResources
    );

    private sealed record RenderCallbackEvent(
        string Kind,
        long Timestamp,
        int ThreadId,
        nint Model,
        int Slot,
        nint Material,
        int MaterialIndex,
        nint MaterialResource
    );
}
#endif
