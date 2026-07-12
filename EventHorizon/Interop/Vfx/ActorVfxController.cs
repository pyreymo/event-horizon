using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace EventHorizon.Interop.Vfx;

internal enum ActorVfxScope
{
    TargetingMeMarker,
}

internal sealed unsafe class ActorVfxController : IDisposable
{
    private static readonly TimeSpan CallbackDrainTimeout = TimeSpan.FromSeconds(2);
    private const string ActorVfxCreateSig =
        "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
    private const string ActorVfxRemoveSig = "0F 11 48 10 48 8D 05";

    private readonly Lock activeVfxSync = new();
    private readonly Dictionary<ActorVfxKey, ActiveVfx> activeVfx = [];
    private readonly IPluginLog log;
    private readonly HookCallbackTracker callbackTracker;
    private ActorVfxRemoveDelegate? actorVfxRemoveOriginal;
    private readonly Hook<ActorVfxRemoveDelegate>? actorVfxRemoveHook;
    private int hookReady;
    private int disposed;

    [Signature(ActorVfxCreateSig)]
    private readonly ActorVfxCreateDelegate? actorVfxCreate = null;

    public ActorVfxController(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, IPluginLog log)
    {
        this.log = log;
        callbackTracker = new HookCallbackTracker(nameof(ActorVfxController), log);

        Hook<ActorVfxRemoveDelegate>? createdHook = null;
        try
        {
            gameInteropProvider.InitializeFromAttributes(this);

            var actorVfxRemoveAddressTemp = sigScanner.ScanText(ActorVfxRemoveSig) + 7;
            var actorVfxRemoveAddress = Marshal.ReadIntPtr(actorVfxRemoveAddressTemp + Marshal.ReadInt32(actorVfxRemoveAddressTemp) + 4);
            createdHook = gameInteropProvider.HookFromAddress<ActorVfxRemoveDelegate>(actorVfxRemoveAddress, ActorVfxRemoveDetour);
            Volatile.Write(ref actorVfxRemoveOriginal, createdHook.Original);
            actorVfxRemoveHook = createdHook;
            callbackTracker.MarkReady();
            createdHook.Enable();
            Volatile.Write(ref hookReady, 1);
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "initialize actor VFX interop");
            Volatile.Write(ref hookReady, 0);
            callbackTracker.BeginStop();
            try
            {
                createdHook?.Dispose();
            }
            catch (Exception disposeException)
            {
                callbackTracker.ReportException(disposeException, "dispose partially initialized actor VFX hook");
            }

            callbackTracker.MarkStopped();
        }
    }

    private delegate VfxObject* ActorVfxCreateDelegate(string path, nint caster, nint target, float a4, char a5, ushort a6, char a7);

    private delegate nint ActorVfxRemoveDelegate(VfxObject* vfx, char a2);

    public void Show(ActorVfxScope scope, ulong gameObjectId, nint actorAddress, string path)
    {
        Show(scope, gameObjectId, actorAddress, actorAddress, path);
    }

    public void Show(ActorVfxScope scope, ulong gameObjectId, nint casterAddress, nint targetAddress, string path)
    {
        if (
            Volatile.Read(ref disposed) != 0
            || gameObjectId == 0
            || casterAddress == nint.Zero
            || targetAddress == nint.Zero
            || string.IsNullOrEmpty(path)
        )
        {
            return;
        }

        var key = new ActorVfxKey(scope, gameObjectId);
        lock (activeVfxSync)
        {
            if (
                activeVfx.TryGetValue(key, out var active)
                && active.CasterAddress == casterAddress
                && active.TargetAddress == targetAddress
                && active.Path == path
            )
            {
                return;
            }
        }

        Remove(key);
        Create(key, casterAddress, targetAddress, path);
    }

    public void PruneScopeExcept(ActorVfxScope scope, HashSet<ulong> gameObjectIds)
    {
        List<ActorVfxKey> keysToRemove;
        lock (activeVfxSync)
        {
            keysToRemove = [];
            foreach (var key in activeVfx.Keys)
            {
                if (key.Scope == scope && !gameObjectIds.Contains(key.GameObjectId))
                {
                    keysToRemove.Add(key);
                }
            }
        }

        foreach (var key in keysToRemove)
        {
            Remove(key);
        }
    }

    public void ClearScope(ActorVfxScope scope)
    {
        List<ActorVfxKey> keysToRemove;
        lock (activeVfxSync)
        {
            keysToRemove = [];
            foreach (var key in activeVfx.Keys)
            {
                if (key.Scope == scope)
                {
                    keysToRemove.Add(key);
                }
            }
        }

        foreach (var key in keysToRemove)
        {
            Remove(key);
        }
    }

    public void Clear()
    {
        List<ActorVfxKey> keys;
        lock (activeVfxSync)
        {
            if (activeVfx.Count == 0)
            {
                return;
            }

            keys = [.. activeVfx.Keys];
        }

        foreach (var key in keys)
        {
            Remove(key);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref hookReady, 0);
        callbackTracker.BeginStop();
        try
        {
            actorVfxRemoveHook?.Disable();
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "disable actor VFX remove hook");
        }

        callbackTracker.WaitForDrain(CallbackDrainTimeout);
        Clear();

        try
        {
            actorVfxRemoveHook?.Dispose();
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "dispose actor VFX remove hook");
        }
        finally
        {
            callbackTracker.MarkStopped();
        }
    }

    private void Create(ActorVfxKey key, nint casterAddress, nint targetAddress, string path)
    {
        var create = actorVfxCreate;
        var removeOriginal = Volatile.Read(ref actorVfxRemoveOriginal);
        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref hookReady) == 0 || create is null || removeOriginal is null)
        {
            return;
        }

        try
        {
            var vfx = create(path, casterAddress, targetAddress, -1f, (char)0, 0, (char)0);
            if (vfx == null)
            {
                return;
            }

            var shouldRemove = false;
            lock (activeVfxSync)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    shouldRemove = true;
                }
                else
                {
                    activeVfx[key] = new ActiveVfx((nint)vfx, casterAddress, targetAddress, path);
                }
            }

            if (shouldRemove)
            {
                removeOriginal(vfx, (char)1);
            }
        }
        catch (Exception ex)
        {
            SafeWarning(ex, "Failed to create actor VFX.");
        }
    }

    private void Remove(ActorVfxKey key)
    {
        ActiveVfx active;
        lock (activeVfxSync)
        {
            if (!activeVfx.Remove(key, out active))
            {
                return;
            }
        }

        var removeOriginal = Volatile.Read(ref actorVfxRemoveOriginal);
        if (removeOriginal is null)
        {
            return;
        }

        try
        {
            removeOriginal((VfxObject*)active.VfxAddress, (char)1);
        }
        catch (Exception ex)
        {
            SafeWarning(ex, "Failed to remove actor VFX.");
        }
    }

    private nint ActorVfxRemoveDetour(VfxObject* vfx, char a2)
    {
        using var callback = callbackTracker.Enter();

        var callOriginal = Volatile.Read(ref actorVfxRemoveOriginal);
        if (callOriginal is null)
        {
            // Preserve the destructor-like return value during Reloaded's construction probe.
            callbackTracker.ReportMissingOriginal();
            return (nint)vfx;
        }

        if (callbackTracker.ShouldRunPluginLogic && Volatile.Read(ref disposed) == 0)
        {
            try
            {
                DropTrackedVfx((nint)vfx);
            }
            catch (Exception ex)
            {
                callbackTracker.ReportException(ex, "drop tracked actor VFX");
            }
        }

        return callOriginal(vfx, a2);
    }

    private void DropTrackedVfx(nint vfxAddress)
    {
        if (vfxAddress == nint.Zero)
        {
            return;
        }

        lock (activeVfxSync)
        {
            ActorVfxKey? removedKey = null;
            foreach (var (key, active) in activeVfx)
            {
                if (active.VfxAddress == vfxAddress)
                {
                    removedKey = key;
                    break;
                }
            }

            if (removedKey.HasValue)
            {
                activeVfx.Remove(removedKey.Value);
            }
        }
    }

    private void SafeWarning(Exception exception, string message)
    {
        try
        {
            log.Warning(exception, message);
        }
        catch
        {
            // Do not let diagnostics interfere with cleanup.
        }
    }

    private readonly record struct ActorVfxKey(ActorVfxScope Scope, ulong GameObjectId);

    private readonly record struct ActiveVfx(nint VfxAddress, nint CasterAddress, nint TargetAddress, string Path);
}
