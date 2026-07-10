using System;
using System.Collections.Generic;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerAdmissionGate
{
    internal const int FirstPlayerSlot = 2;
    internal const int LastPlayerSlot = 198;
    internal const int PlayerSlotStep = 2;

    private readonly PlayerSlotIdentityTracker slotTracker = new();
    private readonly List<PlayerAdmissionChange> changes = [];

    public PlayerAdmissionUpdateResult Apply(
        IReadOnlyList<PlayerObjectIdentity?> currentSlots,
        PlayerVisibilityTargetSet? activeTarget,
        Action<PlayerAdmissionChange> hardHide
    )
    {
        ArgumentNullException.ThrowIfNull(currentSlots);
        ArgumentNullException.ThrowIfNull(hardHide);
        var changeCounts = slotTracker.Evaluate(currentSlots, changes);
        var approvedCount = 0;
        var hiddenCount = 0;
        var failedCount = 0;
        foreach (var change in changes)
        {
            if (change.Kind is not (PlayerAdmissionChangeKind.Appeared or PlayerAdmissionChangeKind.Replaced))
            {
                continue;
            }

            if (IsExplicitlyVisible(activeTarget, change.CurrentIdentity!.Value))
            {
                approvedCount++;
                continue;
            }

            try
            {
                hardHide(change);
                hiddenCount++;
            }
            catch
            {
                failedCount++;
            }
        }

        return new PlayerAdmissionUpdateResult(
            changeCounts.BaselineEstablished,
            changeCounts.AppearedCount,
            changeCounts.ReplacedCount,
            changeCounts.DisappearedCount,
            approvedCount,
            hiddenCount,
            failedCount
        );
    }

    public void ResetTracking() => slotTracker.Reset();

    private static bool IsExplicitlyVisible(PlayerVisibilityTargetSet? activeTarget, PlayerObjectIdentity identity)
    {
        if (activeTarget == null)
        {
            return false;
        }

        foreach (var target in activeTarget.Targets)
        {
            if (target.Identity == identity && target.DesiredVisible)
            {
                return true;
            }
        }

        return false;
    }
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
    int FailedCount
);
