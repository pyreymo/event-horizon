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

    private delegate void* UpdateObjectArraysDelegate(GameObjectManager* objectManager);

    public bool NeedsDynamicRefresh => objectCuller.NeedsDynamicRefresh();
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
        StaticVfxController staticVfxController
    )
    {
        objectCuller = new ObjectCuller(configuration, playerState, condition, objectTable, targetManager, gameGui, staticVfxController);
        hook = gameInteropProvider.HookFromSignature<UpdateObjectArraysDelegate>(Signature, Detour);
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

        OnObjectArraysUpdated(GameObjectManager.Instance(), refreshPlayerPreview);
    }

    public void FrameworkTick()
    {
        objectCuller.Tick(GameObjectManager.Instance());
    }

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
        objectCuller.Dispose();
        hook.Dispose();
    }

    private void* Detour(GameObjectManager* objectManager)
    {
        var result = hook.Original(objectManager);
        objectCuller.ApplyPlayerAdmissionGate(objectManager);

        return result;
    }

    private void OnObjectArraysUpdated(GameObjectManager* objectManager, bool refreshPlayerPreview)
    {
        objectCuller.Update(objectManager, refreshPlayerPreview);
    }
}
