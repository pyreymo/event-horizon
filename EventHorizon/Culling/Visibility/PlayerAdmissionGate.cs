using System;
using System.Collections.Generic;
using System.Threading;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerAdmissionGate
{
    internal const int FirstPlayerSlot = 2;
    internal const int LastPlayerSlot = 198;
    internal const int PlayerSlotStep = 2;

    private readonly PlayerSlotIdentityTracker slotTracker = new();
    private readonly List<PlayerAdmissionChange> changes = [];
    private readonly HashSet<PlayerObjectIdentity> admissionHolds = [];
    private readonly HashSet<PlayerObjectIdentity> newHolds = [];
    private readonly Dictionary<PlayerObjectIdentity, List<int>> currentSlotsByIdentity = [];
    private int requestedResetGeneration;
    private int consumedResetGeneration;
    private int holdCount;
    private long admissionHideFailed;
    private long admissionReasserted;

    public PlayerAdmissionUpdateResult Apply(
        IReadOnlyList<PlayerObjectIdentity?> currentSlots,
        PlayerVisibilityAppliedState appliedState,
        Action<PlayerAdmissionChange> hardHide
    )
    {
        ArgumentNullException.ThrowIfNull(currentSlots);
        ArgumentNullException.ThrowIfNull(appliedState);
        ArgumentNullException.ThrowIfNull(hardHide);
        ConsumeRequestedReset();
        var changeCounts = slotTracker.Evaluate(currentSlots, changes);
        var approvedCount = 0;
        var hiddenCount = 0;
        var failedCount = 0;
        var reassertedCount = 0;
        newHolds.Clear();
        BuildCurrentSlotMap(currentSlots);

        foreach (var change in changes)
        {
            if (change.Kind is PlayerAdmissionChangeKind.Disappeared or PlayerAdmissionChangeKind.Replaced)
            {
                if (change.PreviousIdentity.HasValue)
                {
                    admissionHolds.Remove(change.PreviousIdentity.Value);
                }
            }

            if (change.Kind is not (PlayerAdmissionChangeKind.Appeared or PlayerAdmissionChangeKind.Replaced))
            {
                continue;
            }

            var currentIdentity = change.CurrentIdentity!.Value;
            if (appliedState.IsExplicitlyVisible(currentIdentity, change.Slot))
            {
                admissionHolds.Remove(currentIdentity);
                approvedCount++;
                continue;
            }

            if (admissionHolds.Add(currentIdentity))
            {
                newHolds.Add(currentIdentity);
            }
        }

        if (changeCounts.BaselineEstablished && appliedState.ActiveTarget != null)
        {
            foreach (var identity in currentSlotsByIdentity.Keys)
            {
                if (!IsVisibleAtEveryCurrentSlot(appliedState, identity) && admissionHolds.Add(identity))
                {
                    newHolds.Add(identity);
                }
            }
        }

        admissionHolds.RemoveWhere(identity => !currentSlotsByIdentity.ContainsKey(identity));
        foreach (var identity in currentSlotsByIdentity.Keys)
        {
            if (IsVisibleAtEveryCurrentSlot(appliedState, identity))
            {
                admissionHolds.Remove(identity);
            }
        }

        foreach (var identity in admissionHolds)
        {
            foreach (var slot in currentSlotsByIdentity[identity])
            {
                var change = new PlayerAdmissionChange(
                    slot,
                    newHolds.Contains(identity) ? PlayerAdmissionChangeKind.Appeared : PlayerAdmissionChangeKind.Unchanged,
                    identity,
                    identity
                );
                try
                {
                    hardHide(change);
                    if (newHolds.Contains(identity))
                    {
                        hiddenCount++;
                    }
                    else
                    {
                        reassertedCount++;
                        Interlocked.Increment(ref admissionReasserted);
                    }
                }
                catch
                {
                    failedCount++;
                    Interlocked.Increment(ref admissionHideFailed);
                }
            }
        }

        Volatile.Write(ref holdCount, admissionHolds.Count);

        return new PlayerAdmissionUpdateResult(
            changeCounts.BaselineEstablished,
            changeCounts.AppearedCount,
            changeCounts.ReplacedCount,
            changeCounts.DisappearedCount,
            approvedCount,
            hiddenCount,
            failedCount,
            reassertedCount,
            admissionHolds.Count
        );
    }

    public PlayerAdmissionDiagnostics GetDiagnostics() =>
        new(Interlocked.Read(ref admissionHideFailed), Interlocked.Read(ref admissionReasserted), Volatile.Read(ref holdCount));

    public void RequestReset()
    {
        Interlocked.Increment(ref requestedResetGeneration);
        Volatile.Write(ref holdCount, 0);
    }

    public void ResetTracking()
    {
        slotTracker.Reset();
        admissionHolds.Clear();
        newHolds.Clear();
        currentSlotsByIdentity.Clear();
        Volatile.Write(ref holdCount, 0);
    }

    private void BuildCurrentSlotMap(IReadOnlyList<PlayerObjectIdentity?> currentSlots)
    {
        currentSlotsByIdentity.Clear();
        for (var slot = FirstPlayerSlot; slot <= LastPlayerSlot; slot += PlayerSlotStep)
        {
            if (currentSlots[slot].HasValue)
            {
                var identity = currentSlots[slot]!.Value;
                if (!currentSlotsByIdentity.TryGetValue(identity, out var slots))
                {
                    slots = [];
                    currentSlotsByIdentity.Add(identity, slots);
                }

                slots.Add(slot);
            }
        }
    }

    private void ConsumeRequestedReset()
    {
        var requested = Volatile.Read(ref requestedResetGeneration);
        if (requested == consumedResetGeneration)
        {
            return;
        }

        ResetTracking();
        consumedResetGeneration = requested;
    }

    private bool IsVisibleAtEveryCurrentSlot(PlayerVisibilityAppliedState appliedState, PlayerObjectIdentity identity)
    {
        foreach (var slot in currentSlotsByIdentity[identity])
        {
            if (!appliedState.IsExplicitlyVisible(identity, slot))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class PlayerTopologyDirtySignal
{
    private int dirty;

    public bool IsDirty => Volatile.Read(ref dirty) != 0;

    public void MarkFrom(PlayerAdmissionUpdateResult result)
    {
        if (result.AppearedCount != 0 || result.ReplacedCount != 0 || result.DisappearedCount != 0)
        {
            Interlocked.Exchange(ref dirty, 1);
        }
    }

    public bool Consume() => Interlocked.Exchange(ref dirty, 0) != 0;

    public void Clear() => Interlocked.Exchange(ref dirty, 0);
}

internal sealed class PlayerSlotIdentityTracker
{
    private readonly PlayerObjectIdentity?[] previousSlots = new PlayerObjectIdentity?[PlayerAdmissionGate.LastPlayerSlot + 1];
    private bool hasBaseline;

    public PlayerAdmissionChangeCounts Evaluate(IReadOnlyList<PlayerObjectIdentity?> currentSlots, List<PlayerAdmissionChange> changes)
    {
        ArgumentNullException.ThrowIfNull(currentSlots);
        ArgumentNullException.ThrowIfNull(changes);
        if (currentSlots.Count <= PlayerAdmissionGate.LastPlayerSlot)
        {
            throw new ArgumentException($"Slot snapshot must contain index {PlayerAdmissionGate.LastPlayerSlot}.", nameof(currentSlots));
        }

        changes.Clear();
        if (!hasBaseline)
        {
            CopyTrackedSlots(currentSlots);
            hasBaseline = true;
            return new PlayerAdmissionChangeCounts(BaselineEstablished: true, 0, 0, 0);
        }

        var appearedCount = 0;
        var replacedCount = 0;
        var disappearedCount = 0;
        for (
            var slot = PlayerAdmissionGate.FirstPlayerSlot;
            slot <= PlayerAdmissionGate.LastPlayerSlot;
            slot += PlayerAdmissionGate.PlayerSlotStep
        )
        {
            var previous = previousSlots[slot];
            var current = currentSlots[slot];
            var kind = Classify(previous, current);
            if (kind != PlayerAdmissionChangeKind.Unchanged)
            {
                changes.Add(new PlayerAdmissionChange(slot, kind, previous, current));
            }

            switch (kind)
            {
                case PlayerAdmissionChangeKind.Appeared:
                    appearedCount++;
                    break;
                case PlayerAdmissionChangeKind.Replaced:
                    replacedCount++;
                    break;
                case PlayerAdmissionChangeKind.Disappeared:
                    disappearedCount++;
                    break;
            }

            previousSlots[slot] = current;
        }

        return new PlayerAdmissionChangeCounts(false, appearedCount, replacedCount, disappearedCount);
    }

    public void Reset()
    {
        Array.Clear(previousSlots);
        hasBaseline = false;
    }

    private void CopyTrackedSlots(IReadOnlyList<PlayerObjectIdentity?> currentSlots)
    {
        for (
            var slot = PlayerAdmissionGate.FirstPlayerSlot;
            slot <= PlayerAdmissionGate.LastPlayerSlot;
            slot += PlayerAdmissionGate.PlayerSlotStep
        )
        {
            previousSlots[slot] = currentSlots[slot];
        }
    }

    private static PlayerAdmissionChangeKind Classify(PlayerObjectIdentity? previous, PlayerObjectIdentity? current)
    {
        if (!previous.HasValue)
        {
            return current.HasValue ? PlayerAdmissionChangeKind.Appeared : PlayerAdmissionChangeKind.Unchanged;
        }

        if (!current.HasValue)
        {
            return PlayerAdmissionChangeKind.Disappeared;
        }

        return previous.Value == current.Value ? PlayerAdmissionChangeKind.Unchanged : PlayerAdmissionChangeKind.Replaced;
    }
}

internal readonly record struct PlayerAdmissionChange(
    int Slot,
    PlayerAdmissionChangeKind Kind,
    PlayerObjectIdentity? PreviousIdentity,
    PlayerObjectIdentity? CurrentIdentity
);

internal enum PlayerAdmissionChangeKind
{
    Unchanged,
    Appeared,
    Replaced,
    Disappeared,
}

internal readonly record struct PlayerAdmissionChangeCounts(
    bool BaselineEstablished,
    int AppearedCount,
    int ReplacedCount,
    int DisappearedCount
);

internal readonly record struct PlayerAdmissionUpdateResult(
    bool BaselineEstablished,
    int AppearedCount,
    int ReplacedCount,
    int DisappearedCount,
    int ApprovedCount,
    int HiddenCount,
    int FailedCount,
    int ReassertedCount,
    int HoldCount
);

internal readonly record struct PlayerAdmissionDiagnostics(long AdmissionHideFailed, long AdmissionReasserted, int AdmissionHoldCount);
