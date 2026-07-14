using System;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class UpdateObjectArraysHook : IDisposable
{
    private const string Signature = "40 57 48 83 EC ?? 48 89 5C 24 ?? 33 DB";
    private readonly Hook<UpdateObjectArraysDelegate> hook;
    private readonly Lock topologyLock = new();
    private readonly PlayerObjectIdentity?[] previousPlayers = new PlayerObjectIdentity?[CharacterObjectSlots.LastEvenSlot + 1];
    private int topologyChanged;
    private bool hasBaseline;
    private bool disposed;

    private delegate void* UpdateObjectArraysDelegate(GameObjectManager* objectManager);

    public UpdateObjectArraysHook(IGameInteropProvider gameInteropProvider)
    {
        hook = gameInteropProvider.HookFromSignature<UpdateObjectArraysDelegate>(Signature, Detour);
    }

    public void Enable() => hook.Enable();

    public void Disable()
    {
        if (hook.IsEnabled)
        {
            hook.Disable();
        }
    }

    public bool ConsumePlayerTopologyChanged() => Interlocked.Exchange(ref topologyChanged, 0) != 0;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Disable();
        hook.Dispose();
    }

    private void* Detour(GameObjectManager* objectManager)
    {
        var result = hook.Original(objectManager);
        TrackPlayerTopology(objectManager);
        return result;
    }

    private void TrackPlayerTopology(GameObjectManager* objectManager)
    {
        if (objectManager == null)
        {
            return;
        }

        lock (topologyLock)
        {
            var changed = !hasBaseline;
            for (var slot = CharacterObjectSlots.FirstRemoteSlot; slot <= CharacterObjectSlots.LastEvenSlot; slot += 2)
            {
                var gameObject = objectManager->Objects.IndexSorted[slot].Value;
                PlayerObjectIdentity? current =
                    gameObject != null && gameObject->ObjectKind == ObjectKind.Pc ? PlayerObjectIdentity.From(gameObject) : null;
                if (previousPlayers[slot] == current)
                {
                    continue;
                }

                previousPlayers[slot] = current;
                changed = true;
            }

            hasBaseline = true;
            if (changed)
            {
                Interlocked.Exchange(ref topologyChanged, 1);
            }
        }
    }
}
