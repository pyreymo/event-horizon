using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using EventHorizon.Culling.Rules;
using EventHorizon.Culling.Visibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EventHorizon.Tests;

[TestClass]
public sealed class PlayerVisibilityActiveTargetTests
{
    private static readonly PlayerObjectIdentity IdentityA = Identity(11);
    private static readonly PlayerObjectIdentity IdentityB = Identity(12);
    private static readonly PlayerObjectIdentity IdentityC = Identity(13);
    private static readonly PlayerObjectIdentity IdentityD = Identity(14);
    private static readonly PlayerObjectIdentity IdentityE = Identity(15);

    [TestMethod]
    public void StableTargetBuilder_MapsAllClassificationsAndCutByBudgetCorrectly()
    {
        var entries = new[]
        {
            Entry(IdentityA, 0, PlayerVisibilityClassification.BypassVisible),
            Entry(IdentityB, 1),
            Entry(IdentityC, 2),
            Entry(IdentityD, 3, PlayerVisibilityClassification.ForceHidden),
            Entry(IdentityE, 4, PlayerVisibilityClassification.Unmanaged),
        };

        var plan = Plan(42, entries);
        var target = PlayerVisibilityStableTargetBuilder.Build(plan, Keys(plan, IdentityB), []);

        Assert.AreEqual(42, target.Generation);
        Assert.AreEqual(4, target.Targets.Count);
        AssertTarget(target, IdentityA, desiredVisible: true, cutByBudget: false);
        AssertTarget(target, IdentityB, desiredVisible: true, cutByBudget: false);
        AssertTarget(target, IdentityC, desiredVisible: false, cutByBudget: true);
        AssertTarget(target, IdentityD, desiredVisible: false, cutByBudget: false);
        Assert.IsFalse(target.Targets.Any(value => value.Identity == IdentityE));
    }

    [TestMethod]
    public void StableTargetBuilder_WhenSelectedIdentityIsNotCurrentCompetitive_ThrowsClearly()
    {
        var entries = new[] { Entry(IdentityA, 0), Entry(IdentityB, 1, PlayerVisibilityClassification.ForceHidden) };

        var exception = Assert.Throws<ArgumentException>(() =>
            PlayerVisibilityStableTargetBuilder.Build(
                Plan(1, entries),
                [new PlayerVisibilitySelectionKey(1, IdentityB, entries[1].ObjectIndex)],
                []
            )
        );

        Assert.AreEqual("selectedCompetitiveKeys", exception.ParamName);
        StringAssert.Contains(exception.Message, "does not map to the same Competitive entry");
    }

    [TestMethod]
    public void StableTargetBuilder_DoesNotModifyInputsAndCopiesOutputBuffer()
    {
        var entries = new[] { Entry(IdentityA, 0), Entry(IdentityB, 1) };
        var plan = Plan(7, entries);
        var selected = Keys(plan, IdentityA);
        var buffer = new List<PlayerVisibilityTarget>();
        var originalEntries = plan.Entries.ToArray();
        var originalSelected = selected.ToArray();

        var target = PlayerVisibilityStableTargetBuilder.Build(plan, selected, buffer);
        buffer.Clear();

        CollectionAssert.AreEqual(originalEntries, plan.Entries.ToArray());
        CollectionAssert.AreEqual(originalSelected, selected.ToArray());
        Assert.AreEqual(2, target.Targets.Count);
    }

    [TestMethod]
    public void SourcePolicy_StableReadyUsesStableTarget()
    {
        var decision = PlayerVisibilityTargetSourcePolicy.DecideTargetSource(PlayerVisibilitySelectionStatus.Ready);

        Assert.AreEqual(PlayerVisibilityAppliedSource.StableTopB, decision.AppliedSource);
        Assert.AreEqual(PlayerVisibilityFallbackReason.None, decision.FallbackReason);
    }

    [TestMethod]
    public void SourcePolicy_StableNonReadyFallsBackToLegacy()
    {
        var cases = new[]
        {
            (PlayerVisibilitySelectionStatus.Warmup, PlayerVisibilityFallbackReason.Warmup),
            (PlayerVisibilitySelectionStatus.Unavailable, PlayerVisibilityFallbackReason.Unavailable),
            (PlayerVisibilitySelectionStatus.Failed, PlayerVisibilityFallbackReason.SelectionFailed),
        };

        foreach (var (status, expectedReason) in cases)
        {
            var decision = PlayerVisibilityTargetSourcePolicy.DecideTargetSource(status);
            Assert.AreEqual(PlayerVisibilityAppliedSource.LegacyFallback, decision.AppliedSource);
            Assert.AreEqual(expectedReason, decision.FallbackReason);
        }
    }

    [TestMethod]
    public void ActiveResolver_StableBuildFailureFallsBackToSameLegacyInstance()
    {
        var entries = new[] { Entry(IdentityA, 0) };
        var plan = Plan(1, entries);
        var legacy = Legacy(plan, IdentityA);
        var evaluation = Evaluation(plan, PlayerVisibilitySelectionStatus.Ready, IdentityB);

        var resolution = PlayerVisibilityActiveTargetResolver.Resolve(plan, legacy, evaluation, []);

        Assert.AreSame(legacy, resolution.ActiveTarget);
        Assert.AreEqual(PlayerVisibilityAppliedSource.LegacyFallback, resolution.Evaluation.Trace.AppliedSource);
        Assert.AreEqual(PlayerVisibilityFallbackReason.TargetBuildFailed, resolution.Evaluation.Trace.FallbackReason);
        Assert.AreEqual(PlayerVisibilitySelectionStatus.Failed, resolution.Evaluation.Trace.Status);
    }

    [TestMethod]
    public void ActiveResolver_StableSuccessReturnsCurrentGenerationStableTarget()
    {
        var entries = new[] { Entry(IdentityA, 0), Entry(IdentityB, 1) };
        var plan = Plan(73, entries);
        var legacy = Legacy(plan, IdentityA);

        var resolution = PlayerVisibilityActiveTargetResolver.Resolve(
            plan,
            legacy,
            Evaluation(plan, PlayerVisibilitySelectionStatus.Ready, IdentityB),
            []
        );

        Assert.AreEqual(73, resolution.ActiveTarget.Generation);
        Assert.AreNotSame(legacy, resolution.ActiveTarget);
        AssertTarget(resolution.ActiveTarget, IdentityB, true, false);
        Assert.AreEqual(PlayerVisibilityAppliedSource.StableTopB, resolution.Evaluation.Trace.AppliedSource);
    }

    [TestMethod]
    public void Evaluation_DoesNotCommitHistoryUntilActiveTargetIsExplicitlyCommitted()
    {
        var controller = new PlayerVisibilitySelectionController();
        var entries = new[] { Entry(IdentityA, 0) };
        var plan = Plan(1, entries, milliseconds: 0);
        var legacy = Legacy(plan, IdentityA);

        _ = controller.Evaluate(plan, legacy, true, 1, Vector3.Zero);

        Assert.IsFalse(controller.IsSeeded);
        Assert.AreEqual(0, controller.SelectedHistoryCount);
    }

    [TestMethod]
    public void CommitAppliedTarget_StoresOnlyVisibleCompetitiveIdentities()
    {
        var controller = new PlayerVisibilitySelectionController();
        var entries = new[]
        {
            Entry(IdentityA, 0, PlayerVisibilityClassification.BypassVisible),
            Entry(IdentityB, 1),
            Entry(IdentityC, 2),
            Entry(IdentityD, 3, PlayerVisibilityClassification.ForceHidden),
        };
        var applied = new PlayerVisibilityTargetSet(
            1,
            0,
            [Target(entries[0], true), Target(entries[1], true), Target(entries[2], false), Target(entries[3], false)],
            default
        );

        controller.CommitAppliedTarget(applied);

        Assert.IsTrue(controller.WasApplied(IdentityB));
        Assert.IsFalse(controller.WasApplied(IdentityA));
        Assert.IsFalse(controller.WasApplied(IdentityC));
        Assert.AreEqual(1, controller.SelectedHistoryCount);
    }

    [TestMethod]
    public void FallbackCommit_MakesNextRetentionUseActuallyAppliedLegacySet()
    {
        var controller = new PlayerVisibilitySelectionController();
        var entries = new[] { Entry(IdentityA, 7), Entry(IdentityB, 0) };
        var firstPlan = Plan(1, entries, 0);
        var legacy = Legacy(firstPlan, IdentityA);
        var firstEvaluation = controller.Evaluate(firstPlan, legacy, true, 1, Vector3.Zero);
        Assert.AreEqual(IdentityB, firstEvaluation.SelectedIdentities.Single());
        var fallback = PlayerVisibilityActiveTargetResolver.Resolve(firstPlan, legacy, firstEvaluation, []);
        Assert.AreEqual(PlayerVisibilityFallbackReason.Warmup, fallback.Evaluation.Trace.FallbackReason);
        controller.CommitAppliedTarget(fallback.ActiveTarget);

        var secondPlan = Plan(2, entries, 1_000);
        var secondEvaluation = controller.Evaluate(secondPlan, Legacy(secondPlan, IdentityB), true, 1, new Vector3(5, 0, 0));

        Assert.AreEqual(PlayerVisibilitySelectionStatus.Ready, secondEvaluation.Trace.Status);
        Assert.AreEqual(IdentityA, secondEvaluation.SelectedIdentities.Single());
    }

    [TestMethod]
    public void StableCommit_StoresActuallyAppliedStableSet()
    {
        var controller = new PlayerVisibilitySelectionController();
        var plan = Plan(1, [Entry(IdentityA, 0), Entry(IdentityB, 1)]);
        var stable = PlayerVisibilityStableTargetBuilder.Build(plan, Keys(plan, IdentityB), []);

        controller.CommitAppliedTarget(stable);

        Assert.IsFalse(controller.WasApplied(IdentityA));
        Assert.IsTrue(controller.WasApplied(IdentityB));
    }

    [TestMethod]
    public void ActiveBudgetStats_AreCalculatedFromActiveTarget()
    {
        var entries = new[] { Entry(IdentityA, 0, PlayerVisibilityClassification.BypassVisible), Entry(IdentityB, 1), Entry(IdentityC, 2) };
        var plan = Plan(1, entries);
        var active = PlayerVisibilityStableTargetBuilder.Build(plan, Keys(plan, IdentityC), []);

        var stats = PlayerVisibilityActiveBudgetStats.Calculate(active, 200);

        Assert.AreEqual(1, stats.BudgetExemptPlayerCount);
        Assert.AreEqual(1, stats.VisibleBudgetedPlayerCount);
        Assert.AreEqual(100, stats.VisibleBudgetedPlayerLimit);
    }

    [TestMethod]
    public void ResolutionTrace_DistinguishesProposalChurnFromAppliedFallback()
    {
        var entries = new[] { Entry(IdentityA, 0), Entry(IdentityB, 1) };
        var plan = Plan(1, entries);
        var legacy = Legacy(plan, IdentityA);
        var trace = new PlayerVisibilitySelectionTrace(
            PlayerVisibilitySelectionStatus.Warmup,
            1,
            SelectedCount: 1,
            EnteredCount: 1,
            LeftCount: 1
        );

        var resolution = PlayerVisibilityActiveTargetResolver.Resolve(
            plan,
            legacy,
            new PlayerVisibilitySelectionEvaluation(trace, Keys(plan, IdentityB)),
            []
        );

        Assert.AreEqual(1, resolution.Evaluation.Trace.ProposalSelectedCount);
        Assert.AreEqual(0, resolution.Evaluation.Trace.AppliedSelectedCount);
        Assert.AreEqual(1, resolution.Evaluation.Trace.EnteredCount);
        Assert.AreEqual(1, resolution.Evaluation.Trace.LeftCount);
        Assert.AreEqual(PlayerVisibilityAppliedSource.LegacyFallback, resolution.Evaluation.Trace.AppliedSource);
    }

    [TestMethod]
    public void FailedEvaluationResolutionStillReturnsLegacyForReconciliation()
    {
        var entries = new[] { Entry(IdentityA, 8) };
        var plan = Plan(1, entries);
        var legacy = Legacy(plan, IdentityA);
        var controller = new PlayerVisibilitySelectionController();
        var failedEvaluation = controller.Evaluate(plan, legacy, true, 1, Vector3.Zero);

        var resolution = PlayerVisibilityActiveTargetResolver.Resolve(plan, legacy, failedEvaluation, []);

        Assert.AreEqual(PlayerVisibilitySelectionStatus.Failed, failedEvaluation.Trace.Status);
        Assert.AreSame(legacy, resolution.ActiveTarget);
    }

    private static PlayerVisibilitySelectionEvaluation Evaluation(
        PlayerVisibilityPlan plan,
        PlayerVisibilitySelectionStatus status,
        params PlayerObjectIdentity[] selected
    ) => new(new PlayerVisibilitySelectionTrace(status, 1), Keys(plan, selected));

    private static PlayerVisibilitySelectionKey[] Keys(PlayerVisibilityPlan plan, params PlayerObjectIdentity[] selected) =>
        [
            .. selected.Select(identity =>
            {
                var sourceIndex = plan.Entries.ToList().FindIndex(entry => entry.Identity == identity);
                var entry = sourceIndex >= 0 ? plan.Entries[sourceIndex] : default;
                return new PlayerVisibilitySelectionKey(sourceIndex, identity, entry.ObjectIndex);
            }),
        ];

    private static PlayerVisibilityPlan Plan(int generation, IReadOnlyList<PlayerVisibilityPlanEntry> entries, long milliseconds = 100) =>
        new(generation, milliseconds, entries, default);

    private static PlayerVisibilityPlanEntry Entry(
        PlayerObjectIdentity identity,
        int rank,
        PlayerVisibilityClassification classification = PlayerVisibilityClassification.Competitive
    ) => new(identity, (int)identity.EntityId, classification, Decision(rank), false, new Vector3(identity.EntityId, 0, 0), true);

    private static PlayerVisibilityTargetSet Legacy(PlayerVisibilityPlan plan, params PlayerObjectIdentity[] visible)
    {
        var visibleSet = visible.ToHashSet();
        var targets = plan
            .Entries.Where(entry => entry.IsManaged)
            .Select(entry => Target(entry, visibleSet.Contains(entry.Identity)))
            .ToArray();
        return new PlayerVisibilityTargetSet(plan.Generation, plan.CreatedAtTickCount64, targets, plan.ClassificationCounts);
    }

    private static PlayerVisibilityTarget Target(PlayerVisibilityPlanEntry entry, bool desiredVisible) =>
        new(entry.Identity, entry.ObjectIndex, entry.Classification, desiredVisible, entry.Decision, false);

    private static void AssertTarget(
        PlayerVisibilityTargetSet targetSet,
        PlayerObjectIdentity identity,
        bool desiredVisible,
        bool cutByBudget
    )
    {
        var target = targetSet.Targets.Single(value => value.Identity == identity);
        Assert.AreEqual(desiredVisible, target.DesiredVisible);
        Assert.AreEqual(cutByBudget, target.CutByBudget);
    }

    private static PlayerKeepDecision Decision(int rank) =>
        PlayerKeepDecision.Keep(
            PlayerKeepRuleId.Nearby,
            rank,
            PlayerKeepBudgetPolicy.Counted,
            PlayerKeepTieBreaker.None,
            PlayerKeepRuleMask.None
        );

    private static PlayerObjectIdentity Identity(uint value) => new((nint)value, value, value);
}
