using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    private const string ActorVfxCreateSig =
        "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
    private const string ActorVfxRemoveSig = "0F 11 48 10 48 8D 05";

    private readonly Dictionary<ActorVfxKey, ActiveVfx> activeVfx = [];
    private readonly IPluginLog log;
    private readonly ActorVfxRemoveDelegate? actorVfxRemove;
    private readonly Hook<ActorVfxRemoveDelegate>? actorVfxRemoveHook;
    private bool disposed;

    [Signature(ActorVfxCreateSig)]
    private readonly ActorVfxCreateDelegate? actorVfxCreate = null;

    public ActorVfxController(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, IPluginLog log)
    {
        this.log = log;

        try
        {
            gameInteropProvider.InitializeFromAttributes(this);

            var actorVfxRemoveAddressTemp = sigScanner.ScanText(ActorVfxRemoveSig) + 7;
            var actorVfxRemoveAddress = Marshal.ReadIntPtr(actorVfxRemoveAddressTemp + Marshal.ReadInt32(actorVfxRemoveAddressTemp) + 4);
            actorVfxRemove = Marshal.GetDelegateForFunctionPointer<ActorVfxRemoveDelegate>(actorVfxRemoveAddress);
            actorVfxRemoveHook = gameInteropProvider.HookFromAddress<ActorVfxRemoveDelegate>(actorVfxRemoveAddress, ActorVfxRemoveDetour);
            actorVfxRemoveHook.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to initialize actor VFX interop.");
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
        if (disposed || gameObjectId == 0 || casterAddress == nint.Zero || targetAddress == nint.Zero || string.IsNullOrEmpty(path))
        {
            return;
        }

        var key = new ActorVfxKey(scope, gameObjectId);
        if (
            activeVfx.TryGetValue(key, out var active)
            && active.CasterAddress == casterAddress
            && active.TargetAddress == targetAddress
            && active.Path == path
        )
        {
            return;
        }

        Remove(key);
        Create(key, casterAddress, targetAddress, path);
    }

    public void PruneScopeExcept(ActorVfxScope scope, HashSet<ulong> gameObjectIds)
    {
        var keysToRemove = new List<ActorVfxKey>();
        foreach (var key in activeVfx.Keys)
        {
            if (key.Scope == scope && !gameObjectIds.Contains(key.GameObjectId))
            {
                keysToRemove.Add(key);
            }
        }

        foreach (var key in keysToRemove)
        {
            Remove(key);
        }
    }

    public void ClearScope(ActorVfxScope scope)
    {
        var keysToRemove = new List<ActorVfxKey>();
        foreach (var key in activeVfx.Keys)
        {
            if (key.Scope == scope)
            {
                keysToRemove.Add(key);
            }
        }

        foreach (var key in keysToRemove)
        {
            Remove(key);
        }
    }

    public void Clear()
    {
        if (activeVfx.Count == 0)
        {
            return;
        }

        var keys = new List<ActorVfxKey>(activeVfx.Keys);
        foreach (var key in keys)
        {
            Remove(key);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Clear();
        actorVfxRemoveHook?.Dispose();
        disposed = true;
    }

    private void Create(ActorVfxKey key, nint casterAddress, nint targetAddress, string path)
    {
        if (actorVfxCreate is null || actorVfxRemove is null || actorVfxRemoveHook is null)
        {
            return;
        }

        var vfx = actorVfxCreate(path, casterAddress, targetAddress, -1f, (char)0, 0, (char)0);
        if (vfx != null)
        {
            activeVfx[key] = new ActiveVfx((nint)vfx, casterAddress, targetAddress, path);
        }
    }

    private void Remove(ActorVfxKey key)
    {
        if (!activeVfx.Remove(key, out var active) || actorVfxRemove is null)
        {
            return;
        }

        try
        {
            actorVfxRemove((VfxObject*)active.VfxAddress, (char)1);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to remove actor VFX.");
        }
    }

    private nint ActorVfxRemoveDetour(VfxObject* vfx, char a2)
    {
        DropTrackedVfx((nint)vfx);
        return actorVfxRemoveHook!.Original(vfx, a2);
    }

    private void DropTrackedVfx(nint vfxAddress)
    {
        if (vfxAddress == nint.Zero || activeVfx.Count == 0)
        {
            return;
        }

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

    private readonly record struct ActorVfxKey(ActorVfxScope Scope, ulong GameObjectId);

    private readonly record struct ActiveVfx(nint VfxAddress, nint CasterAddress, nint TargetAddress, string Path);
}
