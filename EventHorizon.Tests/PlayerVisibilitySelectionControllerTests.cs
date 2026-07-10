using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using EventHorizon.Culling.Rules;
using EventHorizon.Culling.Selection;
using EventHorizon.Culling.Visibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EventHorizon.Tests;

[TestClass]
public sealed class PlayerVisibilitySelectionControllerTests
{
    private static readonly PlayerObjectIdentity IdentityA = Identity(1);
    private static readonly PlayerObjectIdentity IdentityB = Identity(2);
    private static readonly PlayerObjectIdentity IdentityC = Identity(3);

    [TestMethod]
    public void FirstEvaluation_SeedsPreviousSelectionFromLegacyOnce()
    {
        var controller = new PlayerVisibilitySelectionController();
        var entries = new[] { Entry(IdentityA, rank: 7), Entry(IdentityB, rank: 0) };

        var result = Evaluate(controller, Plan(1, 0, entries), Legacy(entries, IdentityA), budget: 1, Vector3.Zero);

        Assert.AreEqual(PlayerVisibilitySelectionStatus.Warmup, result.Trace.Status);
        Assert.AreEqual(1, result.Trace.PreviousSelectedCount);
        Assert.AreEqual(IdentityB, result.SelectedIdentities.Single());
        Assert.IsTrue(controller.IsSeeded);
    }

    [TestMethod]
    public void LaterEvaluation_UsesPreviousAppliedSelectionInsteadOfChangedLegacy()
    {
        var controller = new PlayerVisibilitySelectionController();
        var firstEntries = new[] { Entry(IdentityA, rank: 7), Entry(IdentityB, rank: 0) };
        Evaluate(controller, Plan(1, 0, firstEntries), Legacy(firstEntries, IdentityA), 1, Vector3.Zero);
        var secondEntries = new[] { Entry(IdentityA, rank: 0), Entry(IdentityB, rank: 7) };

        var second = Evaluate(controller, Plan(2, 1_000, secondEntries), Legacy(secondEntries, IdentityA), 1, new Vector3(5, 0, 0));

        Assert.AreEqual(PlayerVisibilitySelectionStatus.Ready, second.Trace.Status);
        Assert.AreEqual(IdentityB, second.SelectedIdentities.Single());
        Assert.AreEqual(1, second.Trace.RetainedCount);
        Assert.AreEqual(2, second.Trace.SymmetricDifference);
    }

    [TestMethod]
    public void Reset_CausesNextEvaluationToSeedFromLegacyAgain()
    {
        var controller = new PlayerVisibilitySelectionController();
        var entries = new[] { Entry(IdentityA, rank: 0), Entry(IdentityB, rank: 0) };
        Evaluate(controller, Plan(1, 0, entries), Legacy(entries, IdentityA), 1, Vector3.Zero);
        controller.Reset();

        var result = Evaluate(controller, Plan(2, 1_000, entries), Legacy(entries, IdentityB), 1, Vector3.Zero);

        Assert.AreEqual(IdentityB, result.SelectedIdentities.Single());
        Assert.AreEqual(1, result.Trace.PreviousSelectedCount);
    }

    [TestMethod]
    public void ChangedFullIdentity_DoesNotInheritRetention()
    {
        var controller = new PlayerVisibilitySelectionController();
        var firstEntries = new[] { Entry(IdentityA, rank: 0) };
        Evaluate(controller, Plan(1, 0, firstEntries), Legacy(firstEntries, IdentityA), 1, Vector3.Zero);
        var replacementIdentity = IdentityA with { Address = (nint)999 };
        var secondEntries = new[] { Entry(replacementIdentity, rank: 7), Entry(IdentityB, rank: 0) };

        var result = Evaluate(
            controller,
            Plan(2, 1_000, secondEntries),
            Legacy(secondEntries, replacementIdentity),
            1,
            new Vector3(5, 0, 0)
        );

        Assert.AreEqual(IdentityB, result.SelectedIdentities.Single());
        Assert.AreEqual(1, result.Trace.LeftCount);
        Assert.AreEqual(1, result.Trace.MissingPreviousCount);
    }

    [TestMethod]
    public void MissingPreviousPlayer_CountsAsLeftAndMissing()
    {
        var controller = new PlayerVisibilitySelectionController();
        var firstEntries = new[] { Entry(IdentityA, 0), Entry(IdentityB, 0) };
        Evaluate(controller, Plan(1, 0, firstEntries), Legacy(firstEntries, IdentityA, IdentityB), 2, Vector3.Zero);
        var secondEntries = new[] { Entry(IdentityB, 0), Entry(IdentityC, 7) };

        var result = Evaluate(controller, Plan(2, 1_000, secondEntries), Legacy(secondEntries, IdentityB), 1, new Vector3(5, 0, 0));

        Assert.AreEqual(1, result.Trace.RetainedCount);
        Assert.AreEqual(1, result.Trace.LeftCount);
        Assert.AreEqual(1, result.Trace.MissingPreviousCount);
        Assert.AreEqual(0, result.Trace.ActiveReplacedCount);
    }

    [TestMethod]
    public void StillActivePreviousPlayerThatLosesBudget_CountsAsActiveReplaced()
    {
        var controller = new PlayerVisibilitySelectionController();
        var entries = new[] { Entry(IdentityA, 0), Entry(IdentityB, 1) };
        Evaluate(controller, Plan(1, 0, entries), Legacy(entries, IdentityA, IdentityB), 2, Vector3.Zero);

        var result = Evaluate(controller, Plan(2, 1_000, entries), Legacy(entries, IdentityA), 1, new Vector3(5, 0, 0));

        Assert.AreEqual(1, result.Trace.LeftCount);
        Assert.AreEqual(0, result.Trace.MissingPreviousCount);
        Assert.AreEqual(1, result.Trace.ActiveReplacedCount);
    }

    [TestMethod]
    public void LegacyProposalDifference_IsCalculatedFromCompleteIdentitySets()
    {
        var controller = new PlayerVisibilitySelectionController();
        var entries = new[] { Entry(IdentityA, 7), Entry(IdentityB, 0) };

        var result = Evaluate(controller, Plan(1, 0, entries), Legacy(entries, IdentityA), 1, Vector3.Zero);

        Assert.AreEqual(1, result.Trace.LegacyOnlyCount);
        Assert.AreEqual(1, result.Trace.ProposalOnlyCount);
        Assert.AreEqual(2, result.Trace.SymmetricDifference);
    }

    [TestMethod]
    public void RankHistograms_AreCorrectAndIndependentAcrossEvaluations()
    {
        var controller = new PlayerVisibilitySelectionController();
        var firstEntries = new[] { Entry(IdentityA, 0), Entry(IdentityB, 7), Entry(IdentityC, 7) };
        var first = Evaluate(controller, Plan(1, 0, firstEntries), Legacy(firstEntries, IdentityA), 2, Vector3.Zero);
        var firstCandidateHistogram = first.Trace.CandidateRankHistogram!.ToArray();
        var secondEntries = new[] { Entry(IdentityA, 3) };

        _ = Evaluate(controller, Plan(2, 1_000, secondEntries), Legacy(secondEntries, IdentityA), 1, Vector3.Zero);

        CollectionAssert.AreEqual(new[] { 1, 0, 0, 0, 0, 0, 0, 2 }, firstCandidateHistogram);
        CollectionAssert.AreEqual(firstCandidateHistogram, first.Trace.CandidateRankHistogram!.ToArray());
        Assert.AreEqual(2, first.Trace.ProposalRankHistogram!.Sum());
        Assert.AreEqual(1, first.Trace.LegacyRankHistogram!.Sum());
    }

    [TestMethod]
    public void SelectedSourceIndex_MapsBackToPlanEntryIdentity()
    {
        var controller = new PlayerVisibilitySelectionController();
        var entries = new[] { Entry(IdentityA, 0, PlayerVisibilityClassification.ForceHidden), Entry(IdentityB, 7), Entry(IdentityC, 0) };

        var result = Evaluate(controller, Plan(1, 0, entries), Legacy(entries, IdentityC), 1, Vector3.Zero);

        Assert.AreEqual(IdentityC, result.SelectedIdentities.Single());
    }

    [TestMethod]
    public void UnlimitedVisibility_UsesCompetitiveCandidateCountAsBudget()
    {
        var controller = new PlayerVisibilitySelectionController();
        var entries = new[] { Entry(IdentityA, 0), Entry(IdentityB, 1), Entry(IdentityC, 2) };

        var result = controller.Evaluate(Plan(1, 0, entries), Legacy(entries), false, 1, Vector3.Zero);

        Assert.AreEqual(3, result.Trace.Budget);
        Assert.AreEqual(3, result.Trace.SelectedCount);
    }

    [TestMethod]
    public void FirstLocalSample_MarksTraceWarmupWhileStillSelecting()
    {
        var controller = new PlayerVisibilitySelectionController();
        var entries = new[] { Entry(IdentityA, 0) };

        var result = Evaluate(controller, Plan(1, 0, entries), Legacy(entries, IdentityA), 1, Vector3.Zero);

        Assert.AreEqual(PlayerVisibilitySelectionStatus.Warmup, result.Trace.Status);
        Assert.IsFalse(result.Trace.HasLocalVelocityEstimate);
        Assert.AreEqual(1, result.Trace.SelectedCount);
    }

    [TestMethod]
    public void UnavailableAndFailedEvaluations_DoNotCommitHistory()
    {
        var controller = new PlayerVisibilitySelectionController();
        var validEntries = new[] { Entry(IdentityA, 0) };
        Evaluate(controller, Plan(1, 0, validEntries), Legacy(validEntries, IdentityA), 1, Vector3.Zero);
        var historyCount = controller.SelectedHistoryCount;

        var unavailable = controller.Evaluate(Plan(2, 1_000, validEntries), Legacy(validEntries), true, 1, null);
        var invalidEntries = new[] { Entry(IdentityB, 8) };
        var failed = Evaluate(controller, Plan(3, 2_000, invalidEntries), Legacy(invalidEntries), 1, Vector3.Zero);

        Assert.AreEqual(PlayerVisibilitySelectionStatus.Unavailable, unavailable.Trace.Status);
        Assert.AreEqual(PlayerVisibilitySelectionStatus.Failed, failed.Trace.Status);
        Assert.AreEqual(historyCount, controller.SelectedHistoryCount);
    }

    [TestMethod]
    public void Reset_ClearsSeedHistoryAndAllMotionState()
    {
        var controller = new PlayerVisibilitySelectionController();
        var entries = new[] { Entry(IdentityA, 0) };
        Evaluate(controller, Plan(1, 0, entries), Legacy(entries, IdentityA), 1, Vector3.Zero);
        Evaluate(controller, Plan(2, 1_000, entries), Legacy(entries, IdentityA), 1, new Vector3(1, 0, 0));

        controller.Reset();

        Assert.IsFalse(controller.IsSeeded);
        Assert.AreEqual(0, controller.SelectedHistoryCount);
        Assert.AreEqual(0, controller.TrackedPlayerVelocityCount);

        var afterReset = Evaluate(controller, Plan(3, 2_000, entries), Legacy(entries, IdentityA), 1, new Vector3(100, 0, 0));
        Assert.AreEqual(PlayerVisibilitySelectionStatus.Warmup, afterReset.Trace.Status);
    }

    [TestMethod]
    public void Evaluation_DoesNotModifyPlanOrLegacyInputs()
    {
        var controller = new PlayerVisibilitySelectionController();
        var entries = new[] { Entry(IdentityA, 0), Entry(IdentityB, 1) };
        var plan = Plan(1, 0, entries);
        var legacy = Legacy(entries, IdentityA);
        var originalEntries = plan.Entries.ToArray();
        var originalTargets = legacy.Targets.ToArray();

        _ = Evaluate(controller, plan, legacy, 1, Vector3.Zero);

        CollectionAssert.AreEqual(originalEntries, plan.Entries.ToArray());
        CollectionAssert.AreEqual(originalTargets, legacy.Targets.ToArray());
    }

    [TestMethod]
    public void LocalSpeedSmoother_VectorAndScalarUseSeparateEmaSemantics()
    {
        var parameters = new PlayerVisibilitySelectionParameters(localSpeedHalfLifeSeconds: 0.35);
        var smoother = new LocalSpeedSmoother(parameters);
        smoother.AddSample(TimeSpan.Zero, Vector3.Zero);
        smoother.AddSample(TimeSpan.FromSeconds(0.35), new Vector3(3.5f, 0, 0));
        smoother.AddSample(TimeSpan.FromSeconds(0.70), new Vector3(3.5f, 3.5f, 0));

        Assert.AreEqual(7.5, smoother.SmoothedSpeed, 1e-5);
        Assert.AreEqual(2.5f, smoother.SmoothedVelocity.X, 1e-5f);
        Assert.AreEqual(5.0f, smoother.SmoothedVelocity.Y, 1e-5f);
        Assert.AreNotEqual(smoother.SmoothedSpeed, smoother.SmoothedVelocity.Length(), 0.1);
    }

    [TestMethod]
    public void PlayerVelocityTracker_FirstSampleHasNoEstimateAndContinuousSampleHasDirection()
    {
        var tracker = new PlayerVelocityTracker<string>(PlayerVisibilitySelectionParameters.Default);

        var first = tracker.AddSample(TimeSpan.Zero, "player", Vector3.Zero);
        var second = tracker.AddSample(TimeSpan.FromSeconds(1), "player", new Vector3(5, -2, 1));

        Assert.IsFalse(first.HasVelocityEstimate);
        Assert.IsTrue(second.HasVelocityEstimate);
        Assert.IsTrue(second.Velocity.X > 0);
        Assert.IsTrue(second.Velocity.Y < 0);
        Assert.IsTrue(second.Velocity.Z > 0);
    }

    [TestMethod]
    public void PlayerVelocityTracker_TeleportInvalidatesAndRebuildsBaseline()
    {
        var tracker = new PlayerVelocityTracker<string>(PlayerVisibilitySelectionParameters.Default);
        tracker.AddSample(TimeSpan.Zero, "player", Vector3.Zero);
        tracker.AddSample(TimeSpan.FromSeconds(1), "player", new Vector3(5, 0, 0));

        var teleport = tracker.AddSample(TimeSpan.FromSeconds(2), "player", new Vector3(10_000, 0, 0));
        var rebuilt = tracker.AddSample(TimeSpan.FromSeconds(3), "player", new Vector3(10_005, 0, 0));

        Assert.IsFalse(teleport.HasVelocityEstimate);
        Assert.IsTrue(rebuilt.HasVelocityEstimate);
        Assert.IsTrue(rebuilt.Velocity.X > 0);
    }

    [TestMethod]
    public void PlayerVelocityTracker_DifferentIdentitiesNeverShareState()
    {
        var tracker = new PlayerVelocityTracker<string>(PlayerVisibilitySelectionParameters.Default);
        tracker.AddSample(TimeSpan.Zero, "a", Vector3.Zero);
        tracker.AddSample(TimeSpan.FromSeconds(1), "a", new Vector3(5, 0, 0));

        var firstB = tracker.AddSample(TimeSpan.FromSeconds(1), "b", new Vector3(5, 0, 0));

        Assert.IsTrue(tracker.GetEstimate("a").HasVelocityEstimate);
        Assert.IsFalse(firstB.HasVelocityEstimate);
    }

    [TestMethod]
    public void PlayerVelocityTracker_InvalidPositionAndNonPositiveTimeDoNotPolluteEstimate()
    {
        var tracker = new PlayerVelocityTracker<string>(PlayerVisibilitySelectionParameters.Default);
        tracker.AddSample(TimeSpan.Zero, "player", Vector3.Zero);
        var valid = tracker.AddSample(TimeSpan.FromSeconds(1), "player", new Vector3(5, 0, 0));

        var invalidPosition = tracker.AddSample(TimeSpan.FromSeconds(2), "player", new Vector3(float.NaN, 0, 0));
        var nonPositiveTime = tracker.AddSample(TimeSpan.FromSeconds(1), "player", new Vector3(50, 0, 0));

        Assert.AreEqual(valid, invalidPosition);
        Assert.AreEqual(valid, nonPositiveTime);
    }

    [TestMethod]
    public void PlayerVelocityTracker_PruneRemovesInactiveIdentitiesOnly()
    {
        var tracker = new PlayerVelocityTracker<string>(PlayerVisibilitySelectionParameters.Default);
        tracker.AddSample(TimeSpan.Zero, "a", Vector3.Zero);
        tracker.AddSample(TimeSpan.Zero, "b", Vector3.Zero);

        tracker.PruneExcept(new HashSet<string> { "b" });

        Assert.AreEqual(1, tracker.Count);
        Assert.AreEqual(default, tracker.GetEstimate("a"));
        Assert.AreEqual(new PlayerVelocityEstimate(Vector3.Zero, false), tracker.GetEstimate("b"));
    }

    [TestMethod]
    public void RelativeVelocity_IsExactlyOtherMinusLocalWithUnavailableEstimatesAsZero()
    {
        var other = new Vector3(5, 2, -1);
        var local = new Vector3(1, -3, 4);

        Assert.AreEqual(other - local, PlayerVisibilitySelectionController.CalculateRelativeVelocity(other, true, local, true));
        Assert.AreEqual(other, PlayerVisibilitySelectionController.CalculateRelativeVelocity(other, true, local, false));
        Assert.AreEqual(-local, PlayerVisibilitySelectionController.CalculateRelativeVelocity(other, false, local, true));
    }

    private static PlayerVisibilitySelectionEvaluation Evaluate(
        PlayerVisibilitySelectionController controller,
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet legacy,
        int budget,
        Vector3 localPosition
    )
    {
        var evaluation = controller.Evaluate(plan, legacy, true, budget, localPosition);
        if (evaluation.Trace.Status is PlayerVisibilitySelectionStatus.Ready or PlayerVisibilitySelectionStatus.Warmup)
        {
            var appliedTarget = PlayerVisibilityStableTargetBuilder.Build(plan, evaluation.SelectedIdentities, []);
            controller.CommitAppliedTarget(appliedTarget);
        }

        return evaluation;
    }

    private static PlayerVisibilityPlan Plan(int generation, long milliseconds, IReadOnlyList<PlayerVisibilityPlanEntry> entries) =>
        new(generation, milliseconds, entries, default, default);

    private static PlayerVisibilityPlanEntry Entry(
        PlayerObjectIdentity identity,
        int rank,
        PlayerVisibilityClassification classification = PlayerVisibilityClassification.Competitive,
        Vector3? position = null
    ) =>
        new(
            identity,
            (int)identity.EntityId,
            classification,
            Decision(rank),
            false,
            position ?? new Vector3(30 + identity.EntityId, 0, 0),
            true
        );

    private static PlayerVisibilityTargetSet Legacy(
        IReadOnlyList<PlayerVisibilityPlanEntry> entries,
        params PlayerObjectIdentity[] visibleIdentities
    )
    {
        var visible = visibleIdentities.ToHashSet();
        var targets = entries
            .Where(entry => entry.Classification != PlayerVisibilityClassification.Unmanaged)
            .Select(entry => new PlayerVisibilityTarget(
                entry.Identity,
                entry.ObjectIndex,
                entry.Classification,
                visible.Contains(entry.Identity),
                entry.Decision,
                false
            ))
            .ToArray();
        return new PlayerVisibilityTargetSet(1, 0, targets, default);
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
