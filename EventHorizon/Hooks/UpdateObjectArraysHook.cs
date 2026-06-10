using System;
using Dalamud.Game.Chat;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using EventHorizon.ObjectTable;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Hooks;

internal sealed unsafe class UpdateObjectArraysHook : IDisposable
{
    private const string Signature = "40 57 48 83 EC ?? 48 89 5C 24 ?? 33 DB";

    private readonly ObjectCuller objectCuller;
    private readonly Hook<UpdateObjectArraysDelegate> hook;

    private delegate void* UpdateObjectArraysDelegate(GameObjectManager* objectManager);

    public bool NeedsDynamicRefresh => objectCuller.NeedsDynamicRefresh();
    public int HiddenPlayerCount => objectCuller.GetHiddenPlayerCount();

    public UpdateObjectArraysHook(
        IGameInteropProvider gameInteropProvider,
        Configuration configuration,
        IPlayerState playerState,
        ICondition condition,
        IObjectTable objectTable,
        ITargetManager targetManager
    )
    {
        objectCuller = new ObjectCuller(
            configuration,
            playerState,
            condition,
            objectTable,
            targetManager
        );
        hook = gameInteropProvider.HookFromSignature<UpdateObjectArraysDelegate>(Signature, Detour);
    }

    public void Enable()
    {
        hook.Enable();
    }

    public void Refresh(bool resetRuleState = false)
    {
        if (resetRuleState)
        {
            objectCuller.ClearRuleState();
        }

        OnObjectArraysUpdated(GameObjectManager.Instance());
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
        OnObjectArraysUpdated(objectManager);

        return result;
    }

    private void OnObjectArraysUpdated(GameObjectManager* objectManager)
    {
        objectCuller.Update(objectManager);
    }
}
