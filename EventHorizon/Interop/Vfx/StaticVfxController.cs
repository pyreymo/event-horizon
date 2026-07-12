using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
    private static readonly TimeSpan CallbackDrainTimeout = TimeSpan.FromSeconds(2);
    private const float PositionEpsilonSq = 0.0001f;
    private const float RotationEpsilon = 0.001f;
    private const string StaticVfxPoolName = "Client.System.Scheduler.Instance.VfxObject";
    private const string StaticVfxRunSig = "E8 ?? ?? ?? ?? B0 02 EB 02";
    private const string StaticVfxRemoveSig =
        "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";

    private readonly Lock activeVfxSync = new();
    private readonly Lock pathBytesSync = new();
    private readonly Dictionary<StaticVfxKey, ActiveStaticVfx> activeVfx = [];
    private readonly Dictionary<string, byte[]> vfxPathBytes = [];
    private readonly byte[] staticVfxPoolNameBytes = GetNullTerminatedUtf8Bytes(StaticVfxPoolName);
    private readonly IPluginLog log;
    private readonly HookCallbackTracker callbackTracker;
    private readonly VfxObject.Delegates.Create? staticVfxCreate;
    private StaticVfxRemoveDelegate? staticVfxRemoveOriginal;
    private readonly Hook<StaticVfxRemoveDelegate>? staticVfxRemoveHook;
    private int hookReady;
    private int loggedInteropUnavailable;
    private int disposed;

    [Signature(StaticVfxRunSig)]
    private readonly StaticVfxRunDelegate? staticVfxRun = null;

    public StaticVfxController(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, IPluginLog log)
    {
        this.log = log;
        callbackTracker = new HookCallbackTracker(nameof(StaticVfxController), log);

        Hook<StaticVfxRemoveDelegate>? createdHook = null;
        try
        {
            gameInteropProvider.InitializeFromAttributes(this);

            var staticVfxCreateAddress = sigScanner.ScanText(VfxObject.Addresses.Create.String);
            staticVfxCreate = Marshal.GetDelegateForFunctionPointer<VfxObject.Delegates.Create>(staticVfxCreateAddress);

            var staticVfxRemoveAddress = sigScanner.ScanText(StaticVfxRemoveSig);
            createdHook = gameInteropProvider.HookFromAddress<StaticVfxRemoveDelegate>(staticVfxRemoveAddress, StaticVfxRemoveDetour);
            Volatile.Write(ref staticVfxRemoveOriginal, createdHook.Original);
            staticVfxRemoveHook = createdHook;
            callbackTracker.MarkReady();
            createdHook.Enable();
            Volatile.Write(ref hookReady, 1);
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "initialize static VFX interop");
            Volatile.Write(ref hookReady, 0);
            callbackTracker.BeginStop();
            try
            {
                createdHook?.Dispose();
            }
            catch (Exception disposeException)
            {
                callbackTracker.ReportException(disposeException, "dispose partially initialized static VFX hook");
            }

            callbackTracker.MarkStopped();
        }
    }

    private delegate nint StaticVfxRunDelegate(VfxObject* vfx, float a1, uint a2);

    private delegate nint StaticVfxRemoveDelegate(VfxObject* vfx);

    public bool IsActive(StaticVfxScope scope, ulong gameObjectId, string path)
    {
        lock (activeVfxSync)
        {
            return activeVfx.TryGetValue(new(scope, gameObjectId), out var active) && active.Path == path && active.VfxAddress != nint.Zero;
        }
    }

    public void ShowOrUpdate(StaticVfxScope scope, ulong gameObjectId, string path, SystemVector3 position, float rotation = 0f)
    {
        if (Volatile.Read(ref disposed) != 0 || gameObjectId == 0 || string.IsNullOrEmpty(path))
        {
            return;
        }

        var key = new StaticVfxKey(scope, gameObjectId);
        ActiveStaticVfx? existing = null;
        lock (activeVfxSync)
        {
            if (activeVfx.TryGetValue(key, out var active) && active.Path == path && active.VfxAddress != nint.Zero)
            {
                existing = active;
            }
        }

        if (existing is { } current)
        {
            if (current.IsSameTransform(position, rotation))
            {
                return;
            }

            try
            {
                UpdateTransform((VfxObject*)current.VfxAddress, position, rotation);
                lock (activeVfxSync)
                {
                    if (activeVfx.TryGetValue(key, out var latest) && latest.VfxAddress == current.VfxAddress)
                    {
                        activeVfx[key] = latest with { Position = position, Rotation = rotation };
                    }
                }
            }
            catch (Exception ex)
            {
                SafeWarning(ex, "[HiddenVfx] Failed to update static VFX transform.");
            }

            return;
        }

        Remove(key);
        Create(key, path, position, rotation);
    }

    public void PruneScopeExcept(StaticVfxScope scope, HashSet<ulong> gameObjectIds)
    {
        List<StaticVfxKey> keysToRemove;
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

    public void ClearScope(StaticVfxScope scope)
    {
        List<StaticVfxKey> keysToRemove;
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
        List<StaticVfxKey> keys;
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
            staticVfxRemoveHook?.Disable();
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "disable static VFX remove hook");
        }

        callbackTracker.WaitForDrain(CallbackDrainTimeout);
        Clear();

        try
        {
            staticVfxRemoveHook?.Dispose();
        }
        catch (Exception ex)
        {
            callbackTracker.ReportException(ex, "dispose static VFX remove hook");
        }
        finally
        {
            callbackTracker.MarkStopped();
        }
    }

    private void Create(StaticVfxKey key, string path, SystemVector3 position, float rotation)
    {
        var create = staticVfxCreate;
        var run = staticVfxRun;
        var removeOriginal = Volatile.Read(ref staticVfxRemoveOriginal);
        if (
            Volatile.Read(ref disposed) != 0
            || Volatile.Read(ref hookReady) == 0
            || create is null
            || run is null
            || removeOriginal is null
        )
        {
            if (Interlocked.Exchange(ref loggedInteropUnavailable, 1) == 0)
            {
                SafeWarning(
                    "[HiddenVfx] Static VFX interop unavailable. createReady={CreateReady} runReady={RunReady} removeReady={RemoveReady}",
                    create is not null,
                    run is not null,
                    removeOriginal is not null
                );
            }

            return;
        }

        VfxObject* vfx = null;
        try
        {
            var pathBytes = GetPathBytes(path);
            fixed (byte* pathPtr = pathBytes)
            fixed (byte* poolPtr = staticVfxPoolNameBytes)
            {
                vfx = create(new CStringPointer(pathPtr), new CStringPointer(poolPtr));
            }

            if (vfx == null)
            {
                SafeWarning("[HiddenVfx] VfxObject.Create returned null. gameObjectId={GameObjectId} path={Path}", key.GameObjectId, path);
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
                    activeVfx[key] = new ActiveStaticVfx((nint)vfx, path, position, rotation);
                }
            }

            if (shouldRemove)
            {
                removeOriginal(vfx);
                return;
            }

            run(vfx, 0f, 0xFFFFFFFF);
            UpdateTransform(vfx, position, rotation);
        }
        catch (Exception ex)
        {
            SafeWarning(ex, "[HiddenVfx] Failed to create static VFX. gameObjectId={GameObjectId} path={Path}", key.GameObjectId, path);
            lock (activeVfxSync)
            {
                activeVfx.Remove(key);
            }

            if (vfx != null)
            {
                try
                {
                    removeOriginal(vfx);
                }
                catch (Exception removeException)
                {
                    SafeWarning(removeException, "[HiddenVfx] Failed to clean up a partially created static VFX.");
                }
            }
        }
    }

    private byte[] GetPathBytes(string path)
    {
        lock (pathBytesSync)
        {
            if (vfxPathBytes.TryGetValue(path, out var bytes))
            {
                return bytes;
            }

            bytes = GetNullTerminatedUtf8Bytes(path);
            vfxPathBytes[path] = bytes;
            return bytes;
        }
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
        ActiveStaticVfx active;
        lock (activeVfxSync)
        {
            if (!activeVfx.Remove(key, out active))
            {
                return false;
            }
        }

        var removeOriginal = Volatile.Read(ref staticVfxRemoveOriginal);
        if (removeOriginal is null)
        {
            return false;
        }

        try
        {
            removeOriginal((VfxObject*)active.VfxAddress);
            return true;
        }
        catch (Exception ex)
        {
            SafeWarning(ex, "Failed to remove static VFX.");
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
        using var callback = callbackTracker.Enter();

        var callOriginal = Volatile.Read(ref staticVfxRemoveOriginal);
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
                callbackTracker.ReportException(ex, "drop tracked static VFX");
            }
        }

        return callOriginal(vfx);
    }

    private void DropTrackedVfx(nint vfxAddress)
    {
        if (vfxAddress == nint.Zero)
        {
            return;
        }

        lock (activeVfxSync)
        {
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
    }

    private void SafeWarning(Exception exception, string messageTemplate, params object[] values)
    {
        try
        {
            log.Warning(exception, messageTemplate, values);
        }
        catch
        {
            // Do not let diagnostics interfere with cleanup.
        }
    }

    private void SafeWarning(string messageTemplate, params object[] values)
    {
        try
        {
            log.Warning(messageTemplate, values);
        }
        catch
        {
            // Do not let diagnostics interfere with cleanup.
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
