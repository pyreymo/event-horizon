using System;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using EventHorizon.Interop;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class UpdateObjectArraysHook : IDisposable
{
    private static readonly TimeSpan CallbackDrainTimeout = TimeSpan.FromSeconds(2);
    private const string Signature = "40 57 48 83 EC ?? 48 89 5C 24 ?? 33 DB";

    private readonly Hook<UpdateObjectArraysDelegate>? hook;
    private UpdateObjectArraysDelegate? original;
    private readonly AdmissionGateCallback applyAdmissionGate;
    private readonly HookCallbackTracker callbackTracker;
    private int disposed;

    private delegate void* UpdateObjectArraysDelegate(GameObjectManager* objectManager);
    internal delegate void AdmissionGateCallback(GameObjectManager* objectManager);

    public UpdateObjectArraysHook(IGameInteropProvider gameInteropProvider, AdmissionGateCallback applyAdmissionGate, IPluginLog log)
    {
        this.applyAdmissionGate = applyAdmissionGate;
        callbackTracker = new HookCallbackTracker(nameof(UpdateObjectArraysHook), log);

        try
        {
            var createdHook = gameInteropProvider.HookFromSignature<UpdateObjectArraysDelegate>(Signature, Detour);
            Volatile.Write(ref original, createdHook.Original);
            hook = createdHook;
            callbackTracker.MarkReady();
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "initialize");
            callbackTracker.BeginStop();
            callbackTracker.MarkStopped();
        }
    }

    public void Enable()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        try
        {
            hook?.Enable();
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "enable");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        callbackTracker.BeginStop();
        try
        {
            hook?.Disable();
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "disable");
        }

        callbackTracker.WaitForDrain(CallbackDrainTimeout);

        try
        {
            hook?.Dispose();
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "dispose");
        }
        finally
        {
            callbackTracker.MarkStopped();
        }
    }

    private void* Detour(GameObjectManager* objectManager)
    {
        using var callback = callbackTracker.Enter();

        var callOriginal = Volatile.Read(ref original);
        if (callOriginal is null)
        {
            // Reloaded briefly activates a detour while constructing its trampoline.
            callbackTracker.ReportMissingOriginal();
            return objectManager;
        }

        var result = callOriginal(objectManager);
        if (!callbackTracker.ShouldRunPluginLogic || Volatile.Read(ref disposed) != 0)
        {
            return result;
        }

        try
        {
            applyAdmissionGate(objectManager);
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "apply admission gate");
        }

        return result;
    }
}
