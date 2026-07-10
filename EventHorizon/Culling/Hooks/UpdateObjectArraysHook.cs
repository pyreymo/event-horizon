using System;
using Dalamud.Game.Chat;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using EventHorizon.Culling.Rules;
using EventHorizon.Culling.Visibility;
using EventHorizon.Integration.Vfx;
using EventHorizon.Preview;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling.Hooks;

internal sealed unsafe class UpdateObjectArraysHook : IDisposable
{
    // I experimented with rejecting spawn creation at network side.
    //   Signature: 40 53 48 83 EC 20 ?? ?? ?? 49 8B D8 44 8B C1
    // It works, but rejected actors are truly absent and cannot be restored.
    private const string Signature = "40 57 48 83 EC ?? 48 89 5C 24 ?? 33 DB";

    private readonly ObjectCuller objectCuller;
    private readonly Hook<UpdateObjectArraysDelegate> hook;
    private readonly CullingThreadModelProbe threadModelProbe;
    private bool disposed;

    private delegate void* UpdateObjectArraysDelegate(GameObjectManager* objectManager);

    public int HiddenPlayerCount => objectCuller.GetHiddenPlayerCount();
    public PlayerKeepBudgetStats KeepBudgetStats => objectCuller.GetKeepBudgetStats();
    public PlayerPreviewSnapshot PlayerPreviewSnapshot => objectCuller.GetPlayerPreviewSnapshot();
    public CullingPerformanceTrace LastRefreshTrace => objectCuller.LastUpdateTrace;
    public CullingPerformanceTrace LastTickTrace => objectCuller.LastTickTrace;
    public PlayerAdmissionDiagnostics PlayerAdmissionDiagnostics => objectCuller.GetPlayerAdmissionDiagnostics();

    public UpdateObjectArraysHook(
        IGameInteropProvider gameInteropProvider,
        Configuration configuration,
        IPlayerState playerState,
        ICondition condition,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IGameGui gameGui,
        StaticVfxController staticVfxController,
        IPluginLog log
    )
    {
        objectCuller = new ObjectCuller(
            configuration,
            playerState,
            condition,
            objectTable,
            targetManager,
            gameGui,
            staticVfxController,
            log
        );
        hook = gameInteropProvider.HookFromSignature<UpdateObjectArraysDelegate>(Signature, Detour);
        threadModelProbe = new(log);
    }

    public void Enable()
    {
        hook.Enable();
    }

    public void Refresh(bool resetRuleState = false, bool refreshPlayerPreview = false)
    {
        if (resetRuleState)
        {
            objectCuller.ClearRuleState();
        }

        var manager = GameObjectManager.Instance();
        var runtime = objectCuller.SynchronizeRuntimeMode(manager);
        if (runtime.Mode == CullingRuntimeMode.Active)
        {
            OnObjectArraysUpdated(manager, refreshPlayerPreview);
        }
    }

    public void FrameworkTick()
    {
        objectCuller.Tick(GameObjectManager.Instance());
    }

    public void BeginFrameworkFrame() => threadModelProbe.EnterFramework();

    public void EndFrameworkFrame() => threadModelProbe.ExitFramework();

    public CullingRuntimeSynchronization SynchronizeRuntimeMode() => objectCuller.SynchronizeRuntimeMode(GameObjectManager.Instance());

    public bool ConsumePlayerTopologyDirty() => objectCuller.ConsumePlayerTopologyDirty();

    public void RefreshPlayerPreview()
    {
        objectCuller.RefreshPlayerPreview(GameObjectManager.Instance());
    }

    public bool SetPreviewSelectedPlayer(uint? entityId)
    {
        return objectCuller.SetPreviewSelectedPlayer(entityId);
    }

    public void RecordChatMessage(IChatMessage message)
    {
        objectCuller.RecordChatMessage(message);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (hook.IsEnabled)
        {
            hook.Disable();
        }

        hook.Dispose();
        objectCuller.Dispose();
    }

    private void* Detour(GameObjectManager* objectManager)
    {
        threadModelProbe.EnterDetour();
        try
        {
            var result = hook.Original(objectManager);
            objectCuller.ApplyPlayerAdmissionGate(objectManager);
            return result;
        }
        finally
        {
            threadModelProbe.ExitDetour();
        }
    }

    private void OnObjectArraysUpdated(GameObjectManager* objectManager, bool refreshPlayerPreview)
    {
        objectCuller.Update(objectManager, refreshPlayerPreview);
    }
}
