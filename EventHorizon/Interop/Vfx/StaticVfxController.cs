using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using InteropGenerator.Runtime;
using FfxivQuaternion = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using FfxivVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using SystemVector3 = System.Numerics.Vector3;

namespace EventHorizon.Interop.Vfx;

internal enum StaticVfxScope
{
    HiddenPlayerMarker,
}

internal sealed unsafe class StaticVfxController : IDisposable
{
    private const float PositionEpsilonSq = 0.0001f;
    private const float RotationEpsilon = 0.001f;
    private const string StaticVfxPoolName = "Client.System.Scheduler.Instance.VfxObject";
    private const string StaticVfxCreateSig = "E8 ?? ?? ?? ?? F3 0F 10 35 ?? ?? ?? ?? 48 89 43 08";
    private const string StaticVfxRunSig = "E8 ?? ?? ?? ?? B0 02 EB 02";
    private const string StaticVfxRemoveSig =
        "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";
    private readonly Dictionary<StaticVfxKey, ActiveStaticVfx> activeVfx = [];
    private readonly Dictionary<string, byte[]> vfxPathBytes = [];
    private readonly byte[] staticVfxPoolNameBytes = GetNullTerminatedUtf8Bytes(StaticVfxPoolName);
    private readonly IPluginLog log;
    private readonly Hook<StaticVfxRemoveDelegate>? staticVfxRemoveHook;
    private bool loggedInteropUnavailable;
    private bool disposed;

    [Signature(StaticVfxRunSig)]
    private readonly StaticVfxRunDelegate? staticVfxRun = null;

    [Signature(StaticVfxCreateSig)]
    private readonly VfxObject.Delegates.Create? staticVfxCreate = null;

    [Signature(StaticVfxRemoveSig)]
    private readonly nint staticVfxRemoveAddress = nint.Zero;

    public StaticVfxController(IGameInteropProvider gameInteropProvider, IPluginLog log)
    {
        this.log = log;

        try
        {
            gameInteropProvider.InitializeFromAttributes(this);

            if (staticVfxRemoveAddress != nint.Zero)
            {
                staticVfxRemoveHook = gameInteropProvider.HookFromAddress<StaticVfxRemoveDelegate>(
                    staticVfxRemoveAddress,
                    StaticVfxRemoveDetour
                );
                staticVfxRemoveHook.Enable();
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to initialize static VFX interop.");
        }
    }

    private delegate nint StaticVfxRunDelegate(VfxObject* vfx, float a1, uint a2);

    private delegate nint StaticVfxRemoveDelegate(VfxObject* vfx);

    public bool IsActive(StaticVfxScope scope, ulong gameObjectId, string path)
    {
        return activeVfx.TryGetValue(new(scope, gameObjectId), out var active) && active.Path == path && active.VfxAddress != nint.Zero;
    }

    public void ShowOrUpdate(StaticVfxScope scope, ulong gameObjectId, string path, SystemVector3 position, float rotation = 0f)
    {
        if (disposed || gameObjectId == 0 || string.IsNullOrEmpty(path))
        {
            return;
        }

        var key = new StaticVfxKey(scope, gameObjectId);
        if (activeVfx.TryGetValue(key, out var active) && active.Path == path && active.VfxAddress != nint.Zero)
        {
            if (active.IsSameTransform(position, rotation))
            {
                return;
            }

            UpdateTransform((VfxObject*)active.VfxAddress, position, rotation);
            activeVfx[key] = active with { Position = position, Rotation = rotation };
            return;
        }

        Remove(key);
        Create(key, path, position, rotation);
    }

    public void PruneScopeExcept(StaticVfxScope scope, HashSet<ulong> gameObjectIds)
    {
        if (activeVfx.Count == 0)
        {
            return;
        }

        var keysToRemove = new List<StaticVfxKey>();
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

    public void ClearScope(StaticVfxScope scope)
    {
        if (activeVfx.Count == 0)
        {
            return;
        }

        var keysToRemove = new List<StaticVfxKey>();
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

        var keys = new List<StaticVfxKey>(activeVfx.Keys);
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
        staticVfxRemoveHook?.Dispose();
        disposed = true;
    }

    private void Create(StaticVfxKey key, string path, SystemVector3 position, float rotation)
    {
        var create = staticVfxCreate;
        var run = staticVfxRun;
        if (create is null || run is null || staticVfxRemoveHook is null)
        {
            if (!loggedInteropUnavailable)
            {
                log.Warning(
                    "[HiddenVfx] Static VFX interop unavailable. createReady={CreateReady} runReady={RunReady} removeHookReady={RemoveHookReady}",
                    create is not null,
                    run is not null,
                    staticVfxRemoveHook is not null
                );
                loggedInteropUnavailable = true;
            }

            return;
        }

        try
        {
            var pathBytes = GetPathBytes(path);
            VfxObject* vfx;
            fixed (byte* pathPtr = pathBytes)
            fixed (byte* poolPtr = staticVfxPoolNameBytes)
            {
                vfx = create(new CStringPointer(pathPtr), new CStringPointer(poolPtr));
            }

            if (vfx == null)
            {
                log.Warning("[HiddenVfx] VfxObject.Create returned null. gameObjectId={GameObjectId} path={Path}", key.GameObjectId, path);
                return;
            }

            activeVfx[key] = new ActiveStaticVfx((nint)vfx, path, position, rotation);
            run(vfx, 0f, 0xFFFFFFFF);
            UpdateTransform(vfx, position, rotation);
            return;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[HiddenVfx] Failed to create static VFX. gameObjectId={GameObjectId} path={Path}", key.GameObjectId, path);
            activeVfx.Remove(key);
            return;
        }
    }

    private byte[] GetPathBytes(string path)
    {
        if (vfxPathBytes.TryGetValue(path, out var bytes))
        {
            return bytes;
        }

        bytes = GetNullTerminatedUtf8Bytes(path);
        vfxPathBytes[path] = bytes;
        return bytes;
    }

    private static byte[] GetNullTerminatedUtf8Bytes(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var bytes = new byte[byteCount + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    private bool Remove(StaticVfxKey key)
    {
        if (!activeVfx.Remove(key, out var active) || staticVfxRemoveHook is null)
        {
            return false;
        }

        try
        {
            staticVfxRemoveHook.Original((VfxObject*)active.VfxAddress);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to remove static VFX.");
            return false;
        }
    }

    private static void UpdateTransform(VfxObject* vfx, SystemVector3 position, float rotation)
    {
        if (vfx == null)
        {
            return;
        }

        vfx->Position = new FfxivVector3
        {
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        };
        vfx->Rotation = FfxivQuaternion.CreateFromYawPitchRoll(rotation, 0, 0);
        vfx->UpdateTransforms(true);
    }

    private nint StaticVfxRemoveDetour(VfxObject* vfx)
    {
        DropTrackedVfx((nint)vfx);
        return staticVfxRemoveHook!.Original(vfx);
    }

    private void DropTrackedVfx(nint vfxAddress)
    {
        if (vfxAddress == nint.Zero || activeVfx.Count == 0)
        {
            return;
        }

        StaticVfxKey? removedKey = null;
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

    private readonly record struct StaticVfxKey(StaticVfxScope Scope, ulong GameObjectId);

    private readonly record struct ActiveStaticVfx(nint VfxAddress, string Path, SystemVector3 Position, float Rotation)
    {
        public bool IsSameTransform(SystemVector3 position, float rotation)
        {
            return SystemVector3.DistanceSquared(Position, position) <= PositionEpsilonSq
                && Math.Abs(Rotation - rotation) <= RotationEpsilon;
        }
    }
}
