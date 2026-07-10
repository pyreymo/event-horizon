using EventHorizon.Culling;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EventHorizon.Tests;

[TestClass]
public sealed class CullingRuntimeModeTests
{
    [DataTestMethod]
    [DataRow((int)CullingRuntimeMode.SuspendedDuty)]
    [DataRow((int)CullingRuntimeMode.SuspendedLowPlayerCount)]
    [DataRow((int)CullingRuntimeMode.PlayerUnavailable)]
    public void ActiveToInactive_RequestsOneInactiveTransition(int inactiveValue)
    {
        var inactive = (CullingRuntimeMode)inactiveValue;
        var state = new CullingRuntimeModeTransition();
        state.Synchronize(CullingRuntimeMode.Active);

        var first = state.Synchronize(inactive);
        var repeated = state.Synchronize(inactive);

        Assert.IsTrue(first.EnterInactive);
        Assert.IsFalse(repeated.Changed);
        Assert.IsFalse(CullingFrameSchedule.Decide(inactive, true, true).Tick);
    }

    [TestMethod]
    public void InactiveToActive_RebuildsBeforeTicking()
    {
        var state = new CullingRuntimeModeTransition();
        state.Synchronize(CullingRuntimeMode.SuspendedDuty);

        var transition = state.Synchronize(CullingRuntimeMode.Active);
        var schedule = CullingFrameSchedule.Decide(transition.Mode, refreshDue: true, topologyDirty: false);

        Assert.IsTrue(transition.RebuildActive);
        Assert.IsTrue(schedule.Refresh);
        Assert.IsTrue(schedule.Tick);
    }

    [TestMethod]
    public void DisabledAloneClearsLongTermRuleState()
    {
        var state = new CullingRuntimeModeTransition();

        Assert.IsTrue(state.Synchronize(CullingRuntimeMode.Disabled).ClearLongTermRules);
        Assert.IsFalse(state.Synchronize(CullingRuntimeMode.SuspendedDuty).ClearLongTermRules);
    }

    [TestMethod]
    public void ActiveFrameScheduleNeverTicksMoreThanOnce()
    {
        var schedule = CullingFrameSchedule.Decide(CullingRuntimeMode.Active, refreshDue: true, topologyDirty: true);

        Assert.IsTrue(schedule.Refresh);
        Assert.IsTrue(schedule.Tick);
    }
}
