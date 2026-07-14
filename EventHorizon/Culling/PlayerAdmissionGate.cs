using System;
using System.Collections.Generic;
using System.Threading;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class PlayerAdmissionGate
{
    private const long MissingTargetTimeoutMs = 1500;

    private readonly record struct AdmissionHold(
        PlayerObjectIdentity Identity,
        int ObjectIndex,
        bool OwnsModelFlag,
        long HeldFrameworkFrame,
        long HeldTimestamp
    );

    private readonly Lock stateLock = new();
    private readonly HashSet<PlayerObjectIdentity> observedPlayers = [];
    private readonly HashSet<PlayerObjectIdentity> livePlayers = [];
    private readonly List<PlayerObjectIdentity> staleObservedPlayers = [];
    private readonly Dictionary<PlayerObjectIdentity, AdmissionHold> holds = [];
    private int active;
    private int changed;
    private long frameworkFrame;

    public void BeginFrameworkFrame() => Interlocked.Increment(ref frameworkFrame);

    public void Activate(GameObjectManager* manager)
    {
        CollectRemotePlayers(manager, livePlayers);
        lock (stateLock)
        {
            observedPlayers.Clear();
            observedPlayers.UnionWith(livePlayers);
            holds.Clear();
            Interlocked.Exchange(ref changed, 0);
            Volatile.Write(ref active, 1);
        }
    }

    public void Stop(GameObjectManager* manager)
    {
        List<AdmissionHold> stoppedHolds;
        lock (stateLock)
        {
            Volatile.Write(ref active, 0);
            stoppedHolds = [.. holds.Values];
            holds.Clear();
            observedPlayers.Clear();
        }

        Interlocked.Exchange(ref changed, 0);
        foreach (var hold in stoppedHolds)
        {
            var gameObject = FindLivePlayer(manager, hold.Identity, hold.ObjectIndex, out _);
            if (gameObject == null)
            {
                continue;
            }

            if (hold.OwnsModelFlag)
            {
                gameObject->RenderFlags &= ~VisibilityFlags.Model;
            }
        }
    }

    public bool ConsumeChanged() => Interlocked.Exchange(ref changed, 0) != 0;

    public void OnEnableDrawCompleted(GameObject* gameObject)
    {
        if (Volatile.Read(ref active) == 0 || !IsRemotePlayer(gameObject, out var objectIndex))
        {
            return;
        }

        var identity = PlayerObjectIdentity.From(gameObject);
        var frame = Volatile.Read(ref frameworkFrame);

        lock (stateLock)
        {
            if (Volatile.Read(ref active) == 0 || !observedPlayers.Add(identity))
            {
                return;
            }

            if ((gameObject->RenderFlags & VisibilityFlags.Model) == 0)
            {
                gameObject->RenderFlags |= VisibilityFlags.Model;
                holds[identity] = new AdmissionHold(identity, objectIndex, true, frame, Environment.TickCount64);
            }
        }

        Interlocked.Exchange(ref changed, 1);
    }

    public void PruneObservedPlayers(GameObjectManager* manager)
    {
        CollectRemotePlayers(manager, livePlayers);
        lock (stateLock)
        {
            staleObservedPlayers.Clear();
            foreach (var identity in observedPlayers)
            {
                if (!livePlayers.Contains(identity) && !holds.ContainsKey(identity))
                {
                    staleObservedPlayers.Add(identity);
                }
            }

            foreach (var identity in staleObservedPlayers)
            {
                observedPlayers.Remove(identity);
            }
        }
    }

    public void Reconcile(
        GameObjectManager* manager,
        PlayerVisibilityTarget[] activeTarget,
        HiddenObjectTracker hiddenObjects,
        PlayerCuller.ShowTransitionBudget showTransitionBudget
    )
    {
        List<AdmissionHold> currentHolds;
        lock (stateLock)
        {
            currentHolds = [.. holds.Values];
        }

        var now = Environment.TickCount64;
        var currentFrame = Volatile.Read(ref frameworkFrame);
        foreach (var hold in currentHolds)
        {
            var gameObject = FindLivePlayer(manager, hold.Identity, hold.ObjectIndex, out var objectIndex);
            if (gameObject == null)
            {
                RemoveHold(hold);
                continue;
            }

            if (!TryFindTarget(activeTarget, hold.Identity, out var target))
            {
                if (now - hold.HeldTimestamp < MissingTargetTimeoutMs || !RemoveHold(hold))
                {
                    continue;
                }

                if (hold.OwnsModelFlag)
                {
                    gameObject->RenderFlags &= ~VisibilityFlags.Model;
                }
                continue;
            }

            if (!target.DesiredVisible)
            {
                hiddenObjects.AdoptHidden(
                    gameObject,
                    HiddenObjectTracker.PluginHiddenFlags,
                    hold.OwnsModelFlag ? VisibilityFlags.Model : 0,
                    objectIndex
                );
                RemoveHold(hold);
                continue;
            }

            if (!IsLaterFrameworkFrame(currentFrame, hold.HeldFrameworkFrame))
            {
                continue;
            }

            var releaseBefore = gameObject->RenderFlags;
            var startsShow = hold.OwnsModelFlag && (releaseBefore & VisibilityFlags.Model) != 0;
            if (startsShow && !showTransitionBudget.CanStartShow())
            {
                continue;
            }

            if (!RemoveHold(hold))
            {
                continue;
            }

            if (hold.OwnsModelFlag)
            {
                gameObject->RenderFlags &= ~VisibilityFlags.Model;
            }

            if (startsShow)
            {
                showTransitionBudget.ConsumeShow();
            }
        }
    }

    private bool RemoveHold(AdmissionHold hold)
    {
        lock (stateLock)
        {
            return holds.TryGetValue(hold.Identity, out var current) && current == hold && holds.Remove(hold.Identity);
        }
    }

    private static void CollectRemotePlayers(GameObjectManager* manager, HashSet<PlayerObjectIdentity> players)
    {
        players.Clear();
        if (manager == null)
        {
            return;
        }

        for (var index = CharacterObjectSlots.FirstRemoteSlot; index <= CharacterObjectSlots.LastEvenSlot; index += 2)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject != null && gameObject->ObjectKind == ObjectKind.Pc)
            {
                players.Add(PlayerObjectIdentity.From(gameObject));
            }
        }
    }

    private static bool IsRemotePlayer(GameObject* gameObject, out int objectIndex)
    {
        objectIndex = gameObject == null ? -1 : gameObject->ObjectIndex;
        return gameObject != null && gameObject->ObjectKind == ObjectKind.Pc && CharacterObjectSlots.IsEvenSlot(objectIndex);
    }

    private static GameObject* FindLivePlayer(
        GameObjectManager* manager,
        PlayerObjectIdentity identity,
        int expectedIndex,
        out int objectIndex
    )
    {
        objectIndex = expectedIndex;
        if (manager == null)
        {
            return null;
        }

        if (CharacterObjectSlots.IsEvenSlot(expectedIndex))
        {
            var expected = manager->Objects.IndexSorted[expectedIndex].Value;
            if (expected != null && expected->ObjectKind == ObjectKind.Pc && identity.Matches(expected))
            {
                return expected;
            }
        }

        for (var index = CharacterObjectSlots.FirstRemoteSlot; index <= CharacterObjectSlots.LastEvenSlot; index += 2)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject != null && gameObject->ObjectKind == ObjectKind.Pc && identity.Matches(gameObject))
            {
                objectIndex = index;
                return gameObject;
            }
        }

        return null;
    }

    private static bool TryFindTarget(PlayerVisibilityTarget[] targets, PlayerObjectIdentity identity, out PlayerVisibilityTarget target)
    {
        foreach (var candidate in targets)
        {
            if (candidate.Identity == identity)
            {
                target = candidate;
                return true;
            }
        }

        target = default;
        return false;
    }

    internal static bool IsLaterFrameworkFrame(long currentFrameworkFrame, long heldFrameworkFrame) =>
        currentFrameworkFrame > heldFrameworkFrame;
}
