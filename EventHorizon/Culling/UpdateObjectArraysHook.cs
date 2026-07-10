using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class UpdateObjectArraysHook : IDisposable
{
    private const string Signature = "40 57 48 83 EC ?? 48 89 5C 24 ?? 33 DB";
    private readonly Hook<UpdateObjectArraysDelegate> hook;
    private readonly AdmissionGateCallback applyAdmissionGate;
    private bool disposed;

    private delegate void* UpdateObjectArraysDelegate(GameObjectManager* objectManager);
    internal delegate void AdmissionGateCallback(GameObjectManager* objectManager);

    public UpdateObjectArraysHook(IGameInteropProvider gameInteropProvider, AdmissionGateCallback applyAdmissionGate)
    {
        this.applyAdmissionGate = applyAdmissionGate;
        hook = gameInteropProvider.HookFromSignature<UpdateObjectArraysDelegate>(Signature, Detour);
    }

    public void Enable() => hook.Enable();

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
    }

    private void* Detour(GameObjectManager* objectManager)
    {
        var result = hook.Original(objectManager);
        applyAdmissionGate(objectManager);
        return result;
    }
}
