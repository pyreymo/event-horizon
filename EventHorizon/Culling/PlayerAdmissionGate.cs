using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class PlayerAdmissionGate(IPluginLog log)
{
    private const long MissingTargetTimeoutMs = 1500;

    private readonly record struct AdmissionHold(
        PlayerObjectIdentity Identity,
        int ObjectIndex,
        bool OwnsModelFlag,
        long HeldFrameworkFrame,
        long HeldTimestamp
    );

    private readonly record struct TransitionLog(
        AdmissionTransition Transition,
        PlayerObjectIdentity Identity,
        int ObjectIndex,
        VisibilityFlags Before,
        VisibilityFlags After,
        nint DrawObject,
        long FrameworkFrame
    );

    private enum AdmissionTransition
    {
        EnableDrawCompleted,
        HoldCreated,
        ModelAlreadySet,
        ReleasedVisible,
        TransferredHidden,
        TimedOut,
        RestoredStopped,
        ObjectDisappeared,
    }

    private readonly IPluginLog log = log;
    private readonly Lock stateLock = new();
    private readonly HashSet<PlayerObjectIdentity> observedPlayers = [];
    private readonly Dictionary<PlayerObjectIdentity, AdmissionHold> holds = [];
    private readonly ConcurrentQueue<TransitionLog> pendingLogs = new();
    private int active;
    private int changed;
    private long frameworkFrame;

    public void BeginFrameworkFrame()
    {
        Interlocked.Increment(ref frameworkFrame);
        DrainLogs();
    }

    public void Activate(GameObjectManager* manager)
    {
        var baseline = CollectRemotePlayers(manager);
        lock (stateLock)
        {
            observedPlayers.Clear();
            observedPlayers.UnionWith(baseline);
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
            var gameObject = FindLivePlayer(manager, hold.Identity, hold.ObjectIndex, out var objectIndex);
            if (gameObject == null)
            {
                Enqueue(AdmissionTransition.ObjectDisappeared, hold, objectIndex, 0, 0, 0);
                continue;
            }

            var before = gameObject->RenderFlags;
            if (hold.OwnsModelFlag)
            {
                gameObject->RenderFlags &= ~VisibilityFlags.Model;
            }

            Enqueue(AdmissionTransition.RestoredStopped, hold, objectIndex, before, gameObject->RenderFlags, (nint)gameObject->DrawObject);
        }

        DrainLogs();
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
        var before = gameObject->RenderFlags;
        var drawObject = (nint)gameObject->DrawObject;

        lock (stateLock)
        {
            if (Volatile.Read(ref active) == 0 || !observedPlayers.Add(identity))
            {
                return;
            }

            pendingLogs.Enqueue(
                new TransitionLog(AdmissionTransition.EnableDrawCompleted, identity, objectIndex, before, before, drawObject, frame)
            );

            if ((before & VisibilityFlags.Model) != 0)
            {
                pendingLogs.Enqueue(
                    new TransitionLog(AdmissionTransition.ModelAlreadySet, identity, objectIndex, before, before, drawObject, frame)
                );
            }
            else
            {
                gameObject->RenderFlags |= VisibilityFlags.Model;
                holds[identity] = new AdmissionHold(identity, objectIndex, true, frame, Environment.TickCount64);
                pendingLogs.Enqueue(
                    new TransitionLog(
                        AdmissionTransition.HoldCreated,
                        identity,
                        objectIndex,
                        before,
                        gameObject->RenderFlags,
                        drawObject,
                        frame
                    )
                );
            }
        }

        Interlocked.Exchange(ref changed, 1);
    }

    public void PruneObservedPlayers(GameObjectManager* manager)
    {
        var livePlayers = CollectRemotePlayers(manager);
        lock (stateLock)
        {
            observedPlayers.RemoveWhere(identity => !livePlayers.Contains(identity) && !holds.ContainsKey(identity));
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
                if (RemoveHold(hold))
                {
                    Enqueue(AdmissionTransition.ObjectDisappeared, hold, objectIndex, 0, 0, 0);
                }

                continue;
            }

            if (!TryFindTarget(activeTarget, hold.Identity, out var target))
            {
                if (now - hold.HeldTimestamp < MissingTargetTimeoutMs || !RemoveHold(hold))
                {
                    continue;
                }

                var before = gameObject->RenderFlags;
                if (hold.OwnsModelFlag)
                {
                    gameObject->RenderFlags &= ~VisibilityFlags.Model;
                }

                Enqueue(AdmissionTransition.TimedOut, hold, objectIndex, before, gameObject->RenderFlags, (nint)gameObject->DrawObject);
                continue;
            }

            if (!target.DesiredVisible)
            {
                var transferBefore = gameObject->RenderFlags;
                hiddenObjects.AdoptHidden(
                    gameObject,
                    HiddenObjectTracker.PluginHiddenFlags,
                    hold.OwnsModelFlag ? VisibilityFlags.Model : 0,
                    objectIndex
                );
                if (RemoveHold(hold))
                {
                    Enqueue(
                        AdmissionTransition.TransferredHidden,
                        hold,
                        objectIndex,
                        transferBefore,
                        gameObject->RenderFlags,
                        (nint)gameObject->DrawObject
                    );
                }

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

            Enqueue(
                AdmissionTransition.ReleasedVisible,
                hold,
                objectIndex,
                releaseBefore,
                gameObject->RenderFlags,
                (nint)gameObject->DrawObject
            );
        }

        DrainLogs();
    }

    private bool RemoveHold(AdmissionHold hold)
    {
        lock (stateLock)
        {
            return holds.TryGetValue(hold.Identity, out var current) && current == hold && holds.Remove(hold.Identity);
        }
    }

    private void Enqueue(
        AdmissionTransition transition,
        AdmissionHold hold,
        int objectIndex,
        VisibilityFlags before,
        VisibilityFlags after,
        nint drawObject
    ) =>
        pendingLogs.Enqueue(
            new TransitionLog(transition, hold.Identity, objectIndex, before, after, drawObject, Volatile.Read(ref frameworkFrame))
        );

    private void DrainLogs()
    {
        while (pendingLogs.TryDequeue(out var entry))
        {
            var message =
                $"Player admission {GetTransitionText(entry.Transition)}: ObjectIndex={entry.ObjectIndex}, "
                + $"GameObject=0x{entry.Identity.Address.ToInt64():X}, GameObjectId=0x{entry.Identity.GameObjectId:X}, "
                + $"EntityId=0x{entry.Identity.EntityId:X}, RenderFlags=0x{(uint)entry.Before:X}->0x{(uint)entry.After:X}, "
                + $"DrawObject=0x{entry.DrawObject.ToInt64():X}, FrameworkGeneration={entry.FrameworkFrame}.";
            if (entry.Transition == AdmissionTransition.TimedOut)
            {
                log.Warning(message);
            }
            else
            {
                log.Debug(message);
            }
        }
    }

    private static string GetTransitionText(AdmissionTransition transition) =>
        transition switch
        {
            AdmissionTransition.EnableDrawCompleted => "EnableDraw completed",
            AdmissionTransition.HoldCreated => "hold created",
            AdmissionTransition.ModelAlreadySet => "hold skipped because model bit was already set",
            AdmissionTransition.ReleasedVisible => "released as visible",
            AdmissionTransition.TransferredHidden => "transferred to normal hidden state",
            AdmissionTransition.TimedOut => "timed out",
            AdmissionTransition.RestoredStopped => "restored because runtime mode stopped",
            AdmissionTransition.ObjectDisappeared => "identity mismatch or object disappeared",
            _ => transition.ToString(),
        };

    private static HashSet<PlayerObjectIdentity> CollectRemotePlayers(GameObjectManager* manager)
    {
        var players = new HashSet<PlayerObjectIdentity>();
        if (manager == null)
        {
            return players;
        }

        for (var index = CharacterObjectSlots.FirstRemoteSlot; index <= CharacterObjectSlots.LastEvenSlot; index += 2)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject != null && gameObject->ObjectKind == ObjectKind.Pc)
            {
                players.Add(PlayerObjectIdentity.From(gameObject));
            }
        }

        return players;
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
