using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using EventHorizon.Culling.Selection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EventHorizon.Tests;

[TestClass]
public sealed class PlayerVisibilitySelectorTests
{
    private static readonly PlayerVisibilitySelectionParameters DefaultParameters = PlayerVisibilitySelectionParameters.Default;

    [TestMethod]
    public void Select_WhenCandidatesFitBudget_SelectsAll()
    {
        var candidates = new[] { Candidate(0, rank: 7), Candidate(1, rank: 0), Candidate(2, rank: 4) };

        var result = Select(candidates, budget: candidates.Length);

        Assert.AreEqual(candidates.Length, result.SelectedCount);
        CollectionAssert.AreEquivalent(
            candidates.Select(candidate => candidate.SourceIndex).ToArray(),
            result.SelectedSourceIndices.ToArray()
        );
    }

    [TestMethod]
    public void Select_WhenBudgetIsZero_SelectsNone()
    {
        var result = Select([Candidate(0), Candidate(1)], budget: 0);

        Assert.AreEqual(0, result.Budget);
        Assert.AreEqual(0, result.SelectedCount);
        Assert.AreEqual(0, result.SelectedSourceIndices.Count);
    }

    [TestMethod]
    public void Select_WithIdenticalInput_IsDeterministic()
    {
        var candidates = new[]
        {
            Candidate(3, gameObjectId: 30, entityId: 3, objectIndex: 6),
            Candidate(1, gameObjectId: 10, entityId: 1, objectIndex: 2),
            Candidate(2, gameObjectId: 20, entityId: 2, objectIndex: 4),
        };
        var expected = Select(candidates, budget: 2).SelectedSourceIndices.ToArray();

        for (var iteration = 0; iteration < 20; iteration++)
        {
            CollectionAssert.AreEqual(expected, Select(candidates, budget: 2).SelectedSourceIndices.ToArray());
        }
    }

    [TestMethod]
    public void Select_WithZeroRestRetention_EqualsBaseScoreTopB()
    {
        var parameters = Parameters(restRetentionBonus: 0);
        var candidates = new[]
        {
            Candidate(0, rank: 3, position: new Vector3(40, 0, 0), previouslySelected: true),
            Candidate(1, rank: 1, position: new Vector3(30, 0, 0)),
            Candidate(2, rank: 2, position: new Vector3(20, 0, 0), previouslySelected: true),
            Candidate(3, rank: 0, position: new Vector3(10, 0, 0)),
        };

        var result = Select(candidates, budget: 2, parameters: parameters);
        var expected = result
            .RankedCandidates.OrderByDescending(candidate => candidate.BaseScore)
            .Take(2)
            .Select(candidate => candidate.SourceIndex);

        CollectionAssert.AreEqual(expected.ToArray(), result.SelectedSourceIndices.ToArray());
        Assert.AreEqual(0L, result.RetentionBonus);
    }

    [TestMethod]
    public void DefaultParameters_AtRest_HigherRankTierStrictlyDominatesLowerRetainedTier()
    {
        var higherRank = Candidate(0, rank: 0, position: new Vector3(float.MaxValue, 0, 0));
        var lowerRetainedRank = Candidate(1, rank: 1, position: Vector3.Zero, previouslySelected: true);

        var result = Select([lowerRetainedRank, higherRank], budget: 2);
        var higherScore = result.RankedCandidates.Single(candidate => candidate.SourceIndex == 0);
        var lowerScore = result.RankedCandidates.Single(candidate => candidate.SourceIndex == 1);

        Assert.IsTrue(higherScore.BaseScore > lowerScore.AdjustedScore);
    }

    [TestMethod]
    public void DefaultParameters_AtFullMotion_RetainsAllValidPreviousCandidatesWhenTheyFitBudget()
    {
        var candidates = new[]
        {
            Candidate(0, rank: 7, position: new Vector3(100, 0, 0), previouslySelected: true),
            Candidate(1, rank: 7, position: new Vector3(90, 0, 0), previouslySelected: true),
            Candidate(2, rank: 0, position: Vector3.Zero),
            Candidate(3, rank: 0, position: Vector3.Zero),
        };

        var result = Select(candidates, budget: 2, speed: double.PositiveInfinity);

        CollectionAssert.AreEquivalent(new[] { 0, 1 }, result.SelectedSourceIndices.ToArray());
        Assert.AreEqual(2, result.RetainedCount);
    }

    [TestMethod]
    public void DefaultParameters_AtFullMotion_WhenPreviousCandidatesExceedBudget_RetainsExactlyBudgetCount()
    {
        var candidates = new[]
        {
            Candidate(0, rank: 7, previouslySelected: true),
            Candidate(1, rank: 6, previouslySelected: true),
            Candidate(2, rank: 5, previouslySelected: true),
            Candidate(3, rank: 0),
        };

        var result = Select(candidates, budget: 2, speed: double.PositiveInfinity);

        Assert.AreEqual(2, result.SelectedCount);
        Assert.AreEqual(2, result.RetainedCount);
        Assert.IsTrue(result.SelectedSourceIndices.All(sourceIndex => sourceIndex is >= 0 and <= 2));
    }

    [TestMethod]
    public void Select_WhenPreviousCandidateIsMissing_FillsOnlyOpenSlotsWithNewCandidates()
    {
        var candidates = new[]
        {
            Candidate(0, rank: 7, position: new Vector3(100, 0, 0), previouslySelected: true),
            Candidate(1, rank: 0, position: Vector3.Zero),
            Candidate(2, rank: 1, position: Vector3.Zero),
        };

        var result = Select(candidates, budget: 2, speed: double.PositiveInfinity);

        CollectionAssert.AreEqual(new[] { 0, 1 }, result.SelectedSourceIndices.ToArray());
        Assert.AreEqual(1, result.RetainedCount);
        Assert.AreEqual(1, result.EnteredCount);
    }

    [TestMethod]
    public void Select_AsRestRetentionIncreases_RetainedCountDoesNotDecrease()
    {
        var candidates = new[]
        {
            Candidate(0, rank: 2, position: new Vector3(60, 0, 0), previouslySelected: true),
            Candidate(1, rank: 2, position: new Vector3(45, 0, 0), previouslySelected: true),
            Candidate(2, rank: 2, position: Vector3.Zero),
            Candidate(3, rank: 2, position: new Vector3(10, 0, 0)),
        };
        var retainedCounts = new[] { 0L, 500L, 1_500L }
            .Select(bonus => Select(candidates, budget: 2, parameters: Parameters(restRetentionBonus: bonus)).RetainedCount)
            .ToArray();

        for (var i = 1; i < retainedCounts.Length; i++)
        {
            Assert.IsTrue(retainedCounts[i] >= retainedCounts[i - 1]);
        }
    }

    [TestMethod]
    public void Select_OnScoreTie_PrefersPreviousThenStableIdentityKeys()
    {
        var parameters = Parameters(restRetentionBonus: 0);
        var candidates = new[]
        {
            Candidate(5, gameObjectId: 1, entityId: 1, objectIndex: 1),
            Candidate(4, gameObjectId: 99, entityId: 1, objectIndex: 1, previouslySelected: true),
            Candidate(3, gameObjectId: 1, entityId: 1, objectIndex: 1, previouslySelected: true),
            Candidate(2, gameObjectId: 1, entityId: 1, objectIndex: 1, previouslySelected: true),
        };

        var result = Select(candidates, budget: 4, parameters: parameters);

        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5 }, result.SelectedSourceIndices.ToArray());
    }

    [TestMethod]
    public void CalculateSoftScore_ForNormalZeroFarAndMovingInputs_StaysInUnitRange()
    {
        var inputs = new[]
        {
            (Vector3.Zero, Vector3.Zero),
            (new Vector3(20, 5, 1), Vector3.Zero),
            (new Vector3(10_000, 0, 0), Vector3.Zero),
            (new Vector3(50, 0, 0), new Vector3(-10, 2, 0)),
        };

        foreach (var (position, velocity) in inputs)
        {
            var score = PlayerVisibilitySelector.CalculateSoftScore(position, velocity, DefaultParameters);
            Assert.IsTrue(score is >= 0 and <= 1, $"Soft score {score} was outside [0, 1].");
        }
    }

    [TestMethod]
    public void CalculateSoftScore_WithInvalidVelocity_TreatsVelocityAsZero()
    {
        var position = new Vector3(25, 0, 0);
        var invalidVelocity = new Vector3(float.NaN, float.PositiveInfinity, 0);

        var actual = PlayerVisibilitySelector.CalculateSoftScore(position, invalidVelocity, DefaultParameters);
        var expected = PlayerVisibilitySelector.CalculateSoftScore(position, Vector3.Zero, DefaultParameters);

        Assert.AreEqual(expected, actual, 1e-12);
    }

    [TestMethod]
    public void Select_WithInvalidPosition_UsesZeroSoftScoreAndFiniteIntegerScores()
    {
        var result = Select([Candidate(0, position: new Vector3(float.NaN, 0, 0))], budget: 1);
        var scored = result.RankedCandidates.Single();

        Assert.AreEqual(0.0, scored.SoftScore);
        Assert.AreEqual(0L, scored.SoftPoints);
        Assert.AreEqual(DefaultParameters.RankStep * (DefaultParameters.RankCount - 1), scored.BaseScore);
    }

    [TestMethod]
    public void Select_WithRankOutsideConfiguredRange_ThrowsClearException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => Select([Candidate(0, rank: 8)], budget: 1));

        StringAssert.Contains(exception.Message, "Candidate rank must be in [0, 7]");
    }

    [TestMethod]
    public void Parameters_WhenRequiredInvariantIsBroken_ThrowsClearException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerVisibilitySelectionParameters(rankCount: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerVisibilitySelectionParameters(rankStep: 1_500));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerVisibilitySelectionParameters(moveRetentionBonus: 22_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerVisibilitySelectionParameters(predictionSteps: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerVisibilitySelectionParameters(predictionStepSeconds: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerVisibilitySelectionParameters(predictionGamma: 1.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerVisibilitySelectionParameters(distanceSigma: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerVisibilitySelectionParameters(motionStartSpeed: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerVisibilitySelectionParameters(motionFullSpeed: 0.5));
    }

    [TestMethod]
    public void LocalSpeedSmoother_FirstSampleReturnsZero()
    {
        var smoother = new LocalSpeedSmoother(DefaultParameters);

        var speed = smoother.AddSample(TimeSpan.FromSeconds(10), new Vector3(10, 0, 0));

        Assert.AreEqual(0.0, speed);
    }

    [TestMethod]
    public void LocalSpeedSmoother_ForSameTrajectoryAtDifferentIntervals_IsApproximatelyConsistent()
    {
        var fastSamples = SampleConstantSpeed(intervalSeconds: 0.1, durationSeconds: 2, physicalSpeed: 6);
        var slowSamples = SampleConstantSpeed(intervalSeconds: 0.25, durationSeconds: 2, physicalSpeed: 6);

        Assert.AreEqual(fastSamples, slowSamples, 0.01);
    }

    [TestMethod]
    public void LocalSpeedSmoother_TeleportResetsBaselineWithoutSustainedExtremeSpeed()
    {
        var smoother = new LocalSpeedSmoother(DefaultParameters);
        smoother.AddSample(TimeSpan.Zero, Vector3.Zero);
        var beforeTeleport = smoother.AddSample(TimeSpan.FromSeconds(1), new Vector3(5, 0, 0));
        var atTeleport = smoother.AddSample(TimeSpan.FromSeconds(2), new Vector3(10_000, 0, 0));
        var afterTeleport = smoother.AddSample(TimeSpan.FromSeconds(3), new Vector3(10_000, 0, 0));

        Assert.IsTrue(atTeleport < beforeTeleport);
        Assert.IsTrue(afterTeleport < atTeleport);
        Assert.IsTrue(afterTeleport < DefaultParameters.MotionFullSpeed);
    }

    [TestMethod]
    public void LocalSpeedSmoother_WithNonFinitePosition_DoesNotPolluteState()
    {
        var smoother = new LocalSpeedSmoother(DefaultParameters);
        smoother.AddSample(TimeSpan.Zero, Vector3.Zero);

        var invalidResult = smoother.AddSample(TimeSpan.FromSeconds(1), new Vector3(float.NaN, 0, 0));
        var validResult = smoother.AddSample(TimeSpan.FromSeconds(1), new Vector3(5, 0, 0));

        Assert.AreEqual(0.0, invalidResult);
        Assert.IsTrue(validResult > 0);
    }

    [TestMethod]
    public void LocalSpeedSmoother_WithNonPositiveElapsedTime_LeavesBaselineAndSpeedUnchanged()
    {
        var smoother = new LocalSpeedSmoother(DefaultParameters);
        smoother.AddSample(TimeSpan.FromSeconds(1), Vector3.Zero);

        var sameTimestamp = smoother.AddSample(TimeSpan.FromSeconds(1), new Vector3(100, 0, 0));
        var earlierTimestamp = smoother.AddSample(TimeSpan.Zero, new Vector3(100, 0, 0));
        var laterValidSample = smoother.AddSample(TimeSpan.FromSeconds(2), new Vector3(5, 0, 0));

        Assert.AreEqual(0.0, sameTimestamp);
        Assert.AreEqual(0.0, earlierTimestamp);
        Assert.IsTrue(laterValidSample > 0);
    }

    [TestMethod]
    public void Parameters_WithInvalidSmoothingValues_ThrowsClearException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerVisibilitySelectionParameters(localSpeedHalfLifeSeconds: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayerVisibilitySelectionParameters(maxTrustedLocalSpeed: double.NaN));
    }

    [TestMethod]
    public void Select_WithInvalidAndInfiniteLocalSpeeds_UsesDefinedMotionEndpoints()
    {
        var candidate = Candidate(0, previouslySelected: true);

        var nan = Select([candidate], budget: 1, speed: double.NaN);
        var negative = Select([candidate], budget: 1, speed: -1);
        var positiveInfinity = Select([candidate], budget: 1, speed: double.PositiveInfinity);

        Assert.AreEqual(0.0, nan.MotionFactor);
        Assert.AreEqual(0.0, negative.MotionFactor);
        Assert.AreEqual(1.0, positiveInfinity.MotionFactor);
        Assert.AreEqual(DefaultParameters.MoveRetentionBonus, positiveInfinity.RetentionBonus);
    }

    [TestMethod]
    public void Select_DoesNotModifyInputCandidateCollection()
    {
        var candidates = new List<PlayerVisibilitySelectionCandidate>
        {
            Candidate(2, rank: 7),
            Candidate(0, rank: 0),
            Candidate(1, rank: 3),
        };
        var original = candidates.ToArray();

        _ = Select(candidates, budget: 2);

        CollectionAssert.AreEqual(original, candidates.ToArray());
    }

    private static PlayerVisibilitySelectionResult Select(
        IReadOnlyList<PlayerVisibilitySelectionCandidate> candidates,
        int budget,
        double speed = 0,
        PlayerVisibilitySelectionParameters? parameters = null
    ) => PlayerVisibilitySelector.Select(new PlayerVisibilitySelectionSnapshot(budget, speed, candidates), parameters ?? DefaultParameters);

    private static PlayerVisibilitySelectionCandidate Candidate(
        int sourceIndex,
        ulong? gameObjectId = null,
        uint? entityId = null,
        int? objectIndex = null,
        int rank = 0,
        Vector3? position = null,
        Vector3? velocity = null,
        bool previouslySelected = false
    ) =>
        new(
            sourceIndex,
            gameObjectId ?? (ulong)(sourceIndex + 1),
            entityId ?? (uint)(sourceIndex + 1),
            objectIndex ?? sourceIndex,
            rank,
            position ?? new Vector3(30, 0, 0),
            velocity ?? Vector3.Zero,
            previouslySelected
        );

    private static PlayerVisibilitySelectionParameters Parameters(long restRetentionBonus) => new(restRetentionBonus: restRetentionBonus);

    private static double SampleConstantSpeed(double intervalSeconds, double durationSeconds, float physicalSpeed)
    {
        var smoother = new LocalSpeedSmoother(DefaultParameters);
        var sampleCount = (int)Math.Round(durationSeconds / intervalSeconds);
        for (var sample = 0; sample <= sampleCount; sample++)
        {
            var elapsed = sample * intervalSeconds;
            smoother.AddSample(TimeSpan.FromSeconds(elapsed), new Vector3(physicalSpeed * (float)elapsed, 0, 0));
        }

        return smoother.SmoothedSpeed;
    }
}
