using System;
using System.Collections.Generic;
using System.Linq;
using EventHorizon.Culling.Visibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EventHorizon.Tests;

[TestClass]
public sealed class PlayerAdmissionGateTests
{
    private static readonly PlayerObjectIdentity IdentityA = Identity(1);
    private static readonly PlayerObjectIdentity IdentityB = Identity(2);

    [TestMethod]
    public void FirstScan_OnlyEstablishesBaseline()
    {
        var gate = new PlayerAdmissionGate();
        var slots = Slots((2, IdentityA));
        var hidden = new List<PlayerAdmissionChange>();

        var result = gate.Apply(slots, State(), hidden.Add);

        Assert.IsTrue(result.BaselineEstablished);
        Assert.AreEqual(0, result.AppearedCount);
        Assert.AreEqual(0, result.HiddenCount);
        Assert.AreEqual(0, hidden.Count);
    }

    [TestMethod]
    public void LaterScans_DistinguishAppearedReplacedAndDisappeared()
    {
        var tracker = new PlayerSlotIdentityTracker();
        var changes = new List<PlayerAdmissionChange>();
        tracker.Evaluate(Slots((2, IdentityA)), changes);

        var appeared = tracker.Evaluate(Slots((2, IdentityA), (4, IdentityB)), changes);
        Assert.AreEqual(1, appeared.AppearedCount);
        Assert.AreEqual(PlayerAdmissionChangeKind.Appeared, changes.Single().Kind);

        var replacedIdentity = IdentityB with { EntityId = 999 };
        var replaced = tracker.Evaluate(Slots((2, replacedIdentity), (4, IdentityB)), changes);
        Assert.AreEqual(1, replaced.ReplacedCount);
        Assert.AreEqual(PlayerAdmissionChangeKind.Replaced, changes.Single().Kind);

        var disappeared = tracker.Evaluate(Slots((4, IdentityB)), changes);
        Assert.AreEqual(1, disappeared.DisappearedCount);
        Assert.AreEqual(PlayerAdmissionChangeKind.Disappeared, changes.Single().Kind);
    }

    [TestMethod]
    public void FullIdentity_AnyFieldChangeCountsAsReplacement()
    {
        var variants = new[]
        {
            IdentityA with
            {
                Address = (nint)99,
            },
            IdentityA with
            {
                GameObjectId = 99,
            },
            IdentityA with
            {
                EntityId = 99,
            },
        };

        foreach (var variant in variants)
        {
            var tracker = new PlayerSlotIdentityTracker();
            var changes = new List<PlayerAdmissionChange>();
            tracker.Evaluate(Slots((2, IdentityA)), changes);

            var result = tracker.Evaluate(Slots((2, variant)), changes);

            Assert.AreEqual(1, result.ReplacedCount);
            Assert.AreEqual(variant, changes.Single().CurrentIdentity);
        }
    }

    [TestMethod]
    public void Tracker_IgnoresSlotsZeroOneAndOddSlots()
    {
        var tracker = new PlayerSlotIdentityTracker();
        var changes = new List<PlayerAdmissionChange>();
        tracker.Evaluate(Slots(), changes);
        var slots = Slots();
        slots[0] = IdentityA;
        slots[1] = IdentityA;
        slots[3] = IdentityB;

        var result = tracker.Evaluate(slots, changes);

        Assert.AreEqual(0, result.AppearedCount);
        Assert.AreEqual(0, result.ReplacedCount);
        Assert.AreEqual(0, changes.Count);
    }

    [TestMethod]
    public void AppearedPlayer_ExplicitlyVisibleInActiveTargetIsApproved()
    {
        var gate = BaselineGate();
        var hidden = new List<PlayerAdmissionChange>();
        var active = TargetSet((IdentityA, true));

        var result = gate.Apply(Slots((2, IdentityA)), State(active), hidden.Add);

        Assert.AreEqual(1, result.ApprovedCount);
        Assert.AreEqual(0, result.HiddenCount);
        Assert.AreEqual(0, hidden.Count);
    }

    [TestMethod]
    public void AppearedPlayer_NotFoundOrDesiredHiddenUsesImmediateHardHide()
    {
        foreach (var active in new PlayerVisibilityTargetSet?[] { null, TargetSet((IdentityA, false)), TargetSet((IdentityB, true)) })
        {
            var gate = BaselineGate();
            var hardHideCount = 0;

            var result = gate.Apply(Slots((2, IdentityA)), State(active), _ => hardHideCount++);

            Assert.AreEqual(1, result.HiddenCount);
            Assert.AreEqual(1, hardHideCount);
        }
    }

    [TestMethod]
    public void Replacement_DoesNotInheritPreviousSlotIdentityApproval()
    {
        var gate = new PlayerAdmissionGate();
        gate.Apply(Slots((2, IdentityA)), State(TargetSet((IdentityA, true))), _ => Assert.Fail());
        var hidden = new List<PlayerAdmissionChange>();

        var result = gate.Apply(Slots((2, IdentityB)), State(TargetSet((IdentityA, true))), hidden.Add);

        Assert.AreEqual(1, result.ReplacedCount);
        Assert.AreEqual(1, result.HiddenCount);
        Assert.AreEqual(IdentityB, hidden.Single().CurrentIdentity);
    }

    [TestMethod]
    public void AdmissionHide_UsesOnlyProvidedHardHideCallback()
    {
        var gate = BaselineGate();
        var hardHideCount = 0;
        var fadeCount = 0;
        var showBudgetCount = 0;

        var result = gate.Apply(Slots((2, IdentityA)), State(), _ => hardHideCount++);

        Assert.AreEqual(1, hardHideCount);
        Assert.AreEqual(0, fadeCount);
        Assert.AreEqual(0, showBudgetCount);
        Assert.AreEqual(1, result.HiddenCount);
    }

    [TestMethod]
    public void UnchangedIdentity_DoesNotHideRepeatedly()
    {
        var gate = BaselineGate();
        var hiddenCount = 0;
        gate.Apply(Slots((2, IdentityA)), State(), _ => hiddenCount++);

        var unchanged = gate.Apply(Slots((2, IdentityA)), State(), _ => hiddenCount++);

        Assert.AreEqual(1, hiddenCount);
        Assert.AreEqual(0, unchanged.HiddenCount);
        Assert.AreEqual(0, unchanged.ReplacedCount);
    }

    [TestMethod]
    public void ResetTracking_MakesNextScanBaselineOnly()
    {
        var gate = BaselineGate();
        gate.Apply(Slots((2, IdentityA)), State(), _ => { });
        gate.ResetTracking();
        var hiddenCount = 0;

        var result = gate.Apply(Slots((2, IdentityB)), State(), _ => hiddenCount++);

        Assert.IsTrue(result.BaselineEstablished);
        Assert.AreEqual(0, result.HiddenCount);
        Assert.AreEqual(0, hiddenCount);
    }

    [TestMethod]
    public void HardHideFailure_IsCountedAndDoesNotEscapeGate()
    {
        var gate = BaselineGate();

        var result = gate.Apply(Slots((2, IdentityA)), State(), _ => throw new InvalidOperationException("test"));

        Assert.AreEqual(1, result.FailedCount);
        Assert.AreEqual(0, result.HiddenCount);
    }

    [TestMethod]
    public void TopologyDirtySignal_MarksAnySlotTopologyChangeAndClearsAfterRefresh()
    {
        var changeResults = new[]
        {
            new PlayerAdmissionUpdateResult(false, 1, 0, 0, 0, 0, 0),
            new PlayerAdmissionUpdateResult(false, 0, 1, 0, 0, 0, 0),
            new PlayerAdmissionUpdateResult(false, 0, 0, 1, 0, 0, 0),
        };

        foreach (var result in changeResults)
        {
            var signal = new PlayerTopologyDirtySignal();
            signal.MarkFrom(result);
            Assert.IsTrue(signal.IsDirty);
            signal.Clear();
            Assert.IsFalse(signal.IsDirty);
        }

        var unchangedSignal = new PlayerTopologyDirtySignal();
        unchangedSignal.MarkFrom(default);
        Assert.IsFalse(unchangedSignal.IsDirty);
    }

    [TestMethod]
    public void AppliedVisibilityState_CentralizesActiveTargetAdmissionLookupAndClear()
    {
        var state = State(TargetSet((IdentityA, true), (IdentityB, false)));

        Assert.IsTrue(state.IsExplicitlyVisible(IdentityA));
        Assert.IsFalse(state.IsExplicitlyVisible(IdentityB));
        Assert.IsNotNull(state.ActiveTarget);

        state.Clear();

        Assert.IsNull(state.ActiveTarget);
        Assert.IsFalse(state.IsExplicitlyVisible(IdentityA));
    }

    private static PlayerAdmissionGate BaselineGate()
    {
        var gate = new PlayerAdmissionGate();
        gate.Apply(Slots(), State(), _ => Assert.Fail("Baseline must not hide."));
        return gate;
    }

    private static PlayerObjectIdentity?[] Slots(params (int Slot, PlayerObjectIdentity Identity)[] values)
    {
        var slots = new PlayerObjectIdentity?[PlayerAdmissionGate.LastPlayerSlot + 1];
        foreach (var (slot, identity) in values)
        {
            slots[slot] = identity;
        }

        return slots;
    }

    private static PlayerVisibilityTargetSet TargetSet(params (PlayerObjectIdentity Identity, bool DesiredVisible)[] values)
    {
        var targets = values
            .Select(value => new PlayerVisibilityTarget(
                value.Identity,
                (int)value.Identity.EntityId,
                PlayerVisibilityClassification.Competitive,
                value.DesiredVisible,
                default,
                !value.DesiredVisible
            ))
            .ToArray();
        return new PlayerVisibilityTargetSet(1, 0, targets, default);
    }

    private static PlayerVisibilityAppliedState State(PlayerVisibilityTargetSet? target = null)
    {
        var state = new PlayerVisibilityAppliedState();
        if (target != null)
        {
            state.SetActiveTarget(target);
        }

        return state;
    }

    private static PlayerObjectIdentity Identity(uint value) => new((nint)value, value, value);
}
