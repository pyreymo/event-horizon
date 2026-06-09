using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Hooks;

internal sealed unsafe class UpdateObjectArraysHook : IDisposable
{
    private const string Signature = "40 57 48 83 EC ?? 48 89 5C 24 ?? 33 DB";

    private readonly Configuration configuration;
    private readonly Hook<UpdateObjectArraysDelegate> hook;

    private delegate void* UpdateObjectArraysDelegate(GameObjectManager* objectManager);

    public UpdateObjectArraysHook(IGameInteropProvider gameInteropProvider, Configuration configuration)
    {
        this.configuration = configuration;
        hook = gameInteropProvider.HookFromSignature<UpdateObjectArraysDelegate>(
            Signature,
            Detour);
    }

    public void Enable()
    {
        hook.Enable();
    }

    public void Dispose()
    {
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
        if (!configuration.Enabled)
        {
            return;
        }

        // Custom object-culling work belongs here. The original update has already completed.
    }
}
