using System.Linq;
using EventHorizon.Culling.Visibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EventHorizon.Tests;

[TestClass]
public sealed class PlayerVisibilityFrameStateTests
{
    [TestMethod]
    public void ReconciliationActions_AreIndependentAcrossBuilds()
    {
        var reconciler = new PlayerVisibilityReconciler();
        var tracker = new HiddenObjectTracker();
        var first = reconciler.Reconcile(TargetSet(1, Identity(1)), tracker);
        var firstActions = first.Actions.ToArray();

        _ = reconciler.Reconcile(TargetSet(2, Identity(2)), tracker);

        CollectionAssert.AreEqual(firstActions, first.Actions.ToArray());
        Assert.AreEqual(1, first.Generation);
    }

    [TestMethod]
    public void StableTargetBuilder_SelectsOnlyTheRequestedDuplicateIdentitySlot()
    {
        var identity = Identity(1);
        var entries = new[]
        {
            new PlayerVisibilityPlanEntry(identity, 20, PlayerVisibilityClassification.Competitive, default, false, default, false),
            new PlayerVisibilityPlanEntry(identity, 40, PlayerVisibilityClassification.Competitive, default, false, default, false),
        };
        var plan = new PlayerVisibilityPlan(1, 0, entries, default);

        var target = PlayerVisibilityStableTargetBuilder.Build(plan, [new PlayerVisibilitySelectionKey(1, identity, 40)], []);

        Assert.IsFalse(target.Targets.Single(value => value.ObjectIndex == 20).DesiredVisible);
        Assert.IsTrue(target.Targets.Single(value => value.ObjectIndex == 40).DesiredVisible);
    }

    private static PlayerVisibilityTargetSet TargetSet(int generation, PlayerObjectIdentity identity) =>
        new(
            generation,
            0,
            [new PlayerVisibilityTarget(identity, 2, PlayerVisibilityClassification.Competitive, false, default, true)],
            default
        );

    private static PlayerObjectIdentity Identity(uint value) => new((nint)value, value, value);
}
