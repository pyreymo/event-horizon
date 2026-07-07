using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FfxivQuaternion = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using FfxivVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using SystemVector3 = System.Numerics.Vector3;

namespace EventHorizon.Integration.Vfx;

internal sealed unsafe class StaticVfxController : IDisposable
{
    private const float PositionEpsilonSq = 0.0001f;
    private const float RotationEpsilon = 0.001f;
    private const string StaticVfxPoolName = "Client.System.Scheduler.Instance.VfxObject";
    private const string StaticVfxRunSig = "E8 ?? ?? ?? ?? B0 02 EB 02";
    private const string StaticVfxRemoveSig =
        "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";
    private readonly Dictionary<ulong, ActiveStaticVfx> activeVfx = [];
    private readonly IPluginLog log;
    private readonly VfxObject.Delegates.Create? staticVfxCreate;
    private readonly Hook<StaticVfxRemoveDelegate>? staticVfxRemoveHook;
    private bool loggedInteropUnavailable;
    private bool disposed;

    [Signature(StaticVfxRunSig)]
    private readonly StaticVfxRunDelegate? staticVfxRun = null;

    public StaticVfxController(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, IPluginLog log)
    {
        this.log = log;

        try
        {
            gameInteropProvider.InitializeFromAttributes(this);

            var staticVfxCreateAddress = sigScanner.ScanText(VfxObject.Addresses.Create.String);
            staticVfxCreate = Marshal.GetDelegateForFunctionPointer<VfxObject.Delegates.Create>(staticVfxCreateAddress);

            var staticVfxRemoveAddress = sigScanner.ScanText(StaticVfxRemoveSig);
            staticVfxRemoveHook = gameInteropProvider.HookFromAddress<StaticVfxRemoveDelegate>(
                staticVfxRemoveAddress,
                StaticVfxRemoveDetour
            );
            staticVfxRemoveHook.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to initialize static VFX interop.");
        }
    }

    private delegate nint StaticVfxRunDelegate(VfxObject* vfx, float a1, uint a2);

    private delegate nint StaticVfxRemoveDelegate(VfxObject* vfx);

    public int ActiveCount => activeVfx.Count;

    public void Hide(ulong gameObjectId)
    {
        Remove(gameObjectId);
    }

    public void ShowOrUpdate(ulong gameObjectId, string path, SystemVector3 position, float rotation = 0f)
    {
        if (disposed || gameObjectId == 0 || string.IsNullOrEmpty(path))
        {
            return;
        }

        if (activeVfx.TryGetValue(gameObjectId, out var active) && active.Path == path && active.VfxAddress != nint.Zero)
        {
            if (active.IsSameTransform(position, rotation))
            {
                return;
            }

            UpdateTransform((VfxObject*)active.VfxAddress, position, rotation);
            activeVfx[gameObjectId] = active with { Position = position, Rotation = rotation };
            return;
        }

        Remove(gameObjectId);
        Create(gameObjectId, path, position, rotation);
    }

    public void PruneExcept(HashSet<ulong> gameObjectIds)
    {
        var keysToRemove = new List<ulong>();
        foreach (var gameObjectId in activeVfx.Keys)
        {
            if (!gameObjectIds.Contains(gameObjectId))
            {
                keysToRemove.Add(gameObjectId);
            }
        }

        foreach (var gameObjectId in keysToRemove)
        {
            Remove(gameObjectId);
        }
    }

    public void Clear()
    {
        if (activeVfx.Count == 0)
        {
            return;
        }

        var gameObjectIds = new List<ulong>(activeVfx.Keys);
        foreach (var gameObjectId in gameObjectIds)
        {
            Remove(gameObjectId);
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

    private void Create(ulong gameObjectId, string path, SystemVector3 position, float rotation)
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
            var pathString = new Utf8String(path);
            var poolString = new Utf8String(StaticVfxPoolName);
            var vfx = create(pathString.StringPtr, poolString.StringPtr);
            if (vfx == null)
            {
                log.Warning("[HiddenVfx] VfxObject.Create returned null. gameObjectId={GameObjectId} path={Path}", gameObjectId, path);
                return;
            }

            activeVfx[gameObjectId] = new ActiveStaticVfx((nint)vfx, path, position, rotation);
            run(vfx, 0f, 0xFFFFFFFF);
            UpdateTransform(vfx, position, rotation);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[HiddenVfx] Failed to create static VFX. gameObjectId={GameObjectId} path={Path}", gameObjectId, path);
            activeVfx.Remove(gameObjectId);
        }
    }

    private void Remove(ulong gameObjectId)
    {
        if (!activeVfx.Remove(gameObjectId, out var active) || staticVfxRemoveHook is null)
        {
            return;
        }

        try
        {
            staticVfxRemoveHook.Original((VfxObject*)active.VfxAddress);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to remove static VFX.");
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

        ulong? removedGameObjectId = null;
        foreach (var (gameObjectId, active) in activeVfx)
        {
            if (active.VfxAddress == vfxAddress)
            {
                removedGameObjectId = gameObjectId;
                break;
            }
        }

        if (removedGameObjectId.HasValue)
        {
            activeVfx.Remove(removedGameObjectId.Value);
        }
    }

    private readonly record struct ActiveStaticVfx(nint VfxAddress, string Path, SystemVector3 Position, float Rotation)
    {
        public bool IsSameTransform(SystemVector3 position, float rotation)
        {
            return SystemVector3.DistanceSquared(Position, position) <= PositionEpsilonSq
                && Math.Abs(Rotation - rotation) <= RotationEpsilon;
        }
    }
}
