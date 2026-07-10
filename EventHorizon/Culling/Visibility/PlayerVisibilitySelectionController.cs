using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using EventHorizon.Culling.Selection;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerVisibilitySelectionController
{
    private static readonly Vector3 InvalidPosition = new(float.NaN, float.NaN, float.NaN);
    private readonly PlayerVisibilitySelectionParameters parameters;
    private readonly LocalSpeedSmoother localSpeedSmoother;
    private readonly PlayerVelocityTracker<PlayerObjectIdentity> playerVelocityTracker;
    private readonly Action<Exception>? reportFailure;
    private HashSet<PlayerObjectIdentity> selectedHistory = [];
    private bool seeded;

    public PlayerVisibilitySelectionController(
        PlayerVisibilitySelectionParameters? parameters = null,
        Action<Exception>? reportFailure = null
    )
    {
        this.parameters = parameters ?? PlayerVisibilitySelectionParameters.Default;
        localSpeedSmoother = new LocalSpeedSmoother(this.parameters);
        playerVelocityTracker = new PlayerVelocityTracker<PlayerObjectIdentity>(this.parameters);
        this.reportFailure = reportFailure;
    }

    internal bool IsSeeded => seeded;
    internal int SelectedHistoryCount => selectedHistory.Count;
    internal int TrackedPlayerVelocityCount => playerVelocityTracker.Count;

    internal bool WasApplied(PlayerObjectIdentity identity) => selectedHistory.Contains(identity);

    public PlayerVisibilitySelectionEvaluation Evaluate(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet legacyTargetSet,
        bool limitVisiblePlayerCount,
        int visiblePlayerCountLimit,
        Vector3? localPosition
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(legacyTargetSet);

        var totalStart = Stopwatch.GetTimestamp();
        if (!localPosition.HasValue || !IsFinite(localPosition.Value))
        {
            return new PlayerVisibilitySelectionEvaluation(
                CreateUnavailableTrace(plan.Generation, totalStart, "Local player position is unavailable."),
                []
            );
        }

        try
        {
            return EvaluateCore(plan, legacyTargetSet, limitVisiblePlayerCount, visiblePlayerCountLimit, localPosition.Value, totalStart);
        }
        catch (Exception exception)
        {
            reportFailure?.Invoke(exception);
            return CreateFailedEvaluation(plan.Generation, totalStart, exception);
        }
    }

    internal static PlayerVisibilitySelectionEvaluation CreateFailedEvaluation(int generation, long totalStart, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new PlayerVisibilitySelectionEvaluation(
            CreateFailedTrace(generation, totalStart, $"{exception.GetType().Name}: {exception.Message}"),
            []
        );
    }

    public void Reset()
    {
        seeded = false;
        selectedHistory = [];
        localSpeedSmoother.Reset();
        playerVelocityTracker.Clear();
    }

    public void CommitAppliedTarget(PlayerVisibilityTargetSet appliedTarget)
    {
        ArgumentNullException.ThrowIfNull(appliedTarget);
        var appliedSelection = new HashSet<PlayerObjectIdentity>();
        foreach (var target in appliedTarget.Targets)
        {
            if (target.Classification == PlayerVisibilityClassification.Competitive && target.DesiredVisible)
            {
                appliedSelection.Add(target.Identity);
            }
        }

        selectedHistory = appliedSelection;
        seeded = true;
    }

    private PlayerVisibilitySelectionEvaluation EvaluateCore(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet legacyTargetSet,
        bool limitVisiblePlayerCount,
        int visiblePlayerCountLimit,
        Vector3 localPosition,
        long totalStart
    )
    {
        var snapshotStart = Stopwatch.GetTimestamp();
        var timestamp = TimeSpan.FromMilliseconds(plan.CreatedAtTickCount64);
        localSpeedSmoother.AddSample(timestamp, localPosition);

        var competitiveIdentities = new HashSet<PlayerObjectIdentity>();
        var velocityEstimates = new Dictionary<PlayerObjectIdentity, PlayerVelocityEstimate>();
        var candidateRankHistogram = new int[parameters.RankCount];
        var competitiveCount = 0;
        for (var sourceIndex = 0; sourceIndex < plan.Entries.Count; sourceIndex++)
        {
            var entry = plan.Entries[sourceIndex];
            if (entry.Classification != PlayerVisibilityClassification.Competitive)
            {
                continue;
            }

            competitiveCount++;
            competitiveIdentities.Add(entry.Identity);
            candidateRankHistogram[entry.Decision.Rank]++;
            velocityEstimates[entry.Identity] = entry.HasPosition
                ? playerVelocityTracker.AddSample(timestamp, entry.Identity, entry.Position)
                : playerVelocityTracker.GetEstimate(entry.Identity);
        }

        playerVelocityTracker.PruneExcept(competitiveIdentities);

        var previousSelected = seeded ? [.. selectedHistory] : GetLegacySelectedIdentities(legacyTargetSet);
        var candidates = new List<PlayerVisibilitySelectionCandidate>(competitiveCount);
        for (var sourceIndex = 0; sourceIndex < plan.Entries.Count; sourceIndex++)
        {
            var entry = plan.Entries[sourceIndex];
            if (entry.Classification != PlayerVisibilityClassification.Competitive)
            {
                continue;
            }

            var otherVelocityEstimate = velocityEstimates[entry.Identity];
            var relativeVelocity = CalculateRelativeVelocity(
                otherVelocityEstimate.Velocity,
                otherVelocityEstimate.HasVelocityEstimate,
                localSpeedSmoother.SmoothedVelocity,
                localSpeedSmoother.HasVelocityEstimate
            );
            candidates.Add(
                new PlayerVisibilitySelectionCandidate(
                    sourceIndex,
                    entry.Identity.GameObjectId,
                    entry.Identity.EntityId,
                    entry.ObjectIndex,
                    entry.Decision.Rank,
                    entry.HasPosition ? entry.Position - localPosition : InvalidPosition,
                    relativeVelocity,
                    previousSelected.Contains(entry.Identity)
                )
            );
        }

        var budget = limitVisiblePlayerCount ? Math.Clamp(visiblePlayerCountLimit, 1, 100) : candidates.Count;
        var snapshot = new PlayerVisibilitySelectionSnapshot(budget, localSpeedSmoother.SmoothedSpeed, candidates);
        var snapshotTicks = Stopwatch.GetTimestamp() - snapshotStart;

        var selectorStart = Stopwatch.GetTimestamp();
        var selection = PlayerVisibilitySelector.Select(snapshot, parameters);
        var selectorTicks = Stopwatch.GetTimestamp() - selectorStart;

        var currentSelected = new HashSet<PlayerObjectIdentity>();
        var selectedKeys = new PlayerVisibilitySelectionKey[selection.SelectedSourceIndices.Count];
        for (var selectedIndex = 0; selectedIndex < selection.SelectedSourceIndices.Count; selectedIndex++)
        {
            var sourceIndex = selection.SelectedSourceIndices[selectedIndex];
            var entry = plan.Entries[sourceIndex];
            selectedKeys[selectedIndex] = new(sourceIndex, entry.Identity, entry.ObjectIndex);
            currentSelected.Add(entry.Identity);
        }

        var retainedCount = CountIntersection(currentSelected, previousSelected);
        var enteredCount = CountExcept(currentSelected, previousSelected);
        var leftCount = CountExcept(previousSelected, currentSelected);
        var missingPreviousCount = CountExcept(previousSelected, competitiveIdentities);
        var activeReplacedCount = CountActiveReplaced(previousSelected, competitiveIdentities, currentSelected);

        var status = localSpeedSmoother.HasVelocityEstimate
            ? PlayerVisibilitySelectionStatus.Ready
            : PlayerVisibilitySelectionStatus.Warmup;
        var trace = new PlayerVisibilitySelectionTrace(
            status,
            plan.Generation,
            candidates.Count,
            selection.Budget,
            selection.SelectedCount,
            previousSelected.Count,
            retainedCount,
            enteredCount,
            leftCount,
            missingPreviousCount,
            activeReplacedCount,
            localSpeedSmoother.HasVelocityEstimate,
            playerVelocityTracker.Count,
            localSpeedSmoother.SmoothedSpeed,
            selection.MotionFactor,
            selection.RetentionBonus,
            ReadOnly(candidateRankHistogram),
            snapshotTicks,
            selectorTicks,
            Stopwatch.GetTimestamp() - totalStart
        );

        return new PlayerVisibilitySelectionEvaluation(trace, Array.AsReadOnly(selectedKeys));
    }

    private static HashSet<PlayerObjectIdentity> GetLegacySelectedIdentities(PlayerVisibilityTargetSet targetSet)
    {
        var identities = new HashSet<PlayerObjectIdentity>();
        foreach (var target in targetSet.Targets)
        {
            if (target.Classification == PlayerVisibilityClassification.Competitive && target.DesiredVisible)
            {
                identities.Add(target.Identity);
            }
        }

        return identities;
    }

    private static int CountIntersection(IReadOnlySet<PlayerObjectIdentity> left, IReadOnlySet<PlayerObjectIdentity> right)
    {
        var count = 0;
        foreach (var identity in left)
        {
            if (right.Contains(identity))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountExcept(HashSet<PlayerObjectIdentity> left, IReadOnlySet<PlayerObjectIdentity> right) =>
        left.Count - CountIntersection(left, right);

    private static int CountActiveReplaced(
        IReadOnlySet<PlayerObjectIdentity> previous,
        HashSet<PlayerObjectIdentity> competitive,
        HashSet<PlayerObjectIdentity> current
    )
    {
        var count = 0;
        foreach (var identity in previous)
        {
            if (competitive.Contains(identity) && !current.Contains(identity))
            {
                count++;
            }
        }

        return count;
    }

    private static ReadOnlyCollection<int> ReadOnly(int[] values) => Array.AsReadOnly(values);

    private static PlayerVisibilitySelectionTrace CreateUnavailableTrace(int generation, long totalStart, string reason) =>
        new(
            Status: PlayerVisibilitySelectionStatus.Unavailable,
            Generation: generation,
            FailureReason: reason,
            TotalTicks: Stopwatch.GetTimestamp() - totalStart
        );

    private static PlayerVisibilitySelectionTrace CreateFailedTrace(int generation, long totalStart, string reason) =>
        new(
            Status: PlayerVisibilitySelectionStatus.Failed,
            Generation: generation,
            FailureReason: reason,
            TotalTicks: Stopwatch.GetTimestamp() - totalStart
        );

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    internal static Vector3 CalculateRelativeVelocity(
        Vector3 otherVelocity,
        bool hasOtherVelocity,
        Vector3 localVelocity,
        bool hasLocalVelocity
    ) => (hasOtherVelocity ? otherVelocity : Vector3.Zero) - (hasLocalVelocity ? localVelocity : Vector3.Zero);
}

internal sealed record PlayerVisibilitySelectionEvaluation(
    PlayerVisibilitySelectionTrace Trace,
    IReadOnlyList<PlayerVisibilitySelectionKey> SelectedKeys
)
{
    public IReadOnlyList<PlayerObjectIdentity> SelectedIdentities => [.. SelectedKeys.Select(static key => key.Identity)];
}

internal readonly record struct PlayerVisibilitySelectionKey(int SourceIndex, PlayerObjectIdentity Identity, int ObjectIndex);

internal readonly record struct PlayerVisibilitySelectionTrace(
    PlayerVisibilitySelectionStatus Status,
    int Generation,
    int CandidateCount = 0,
    int Budget = 0,
    int SelectedCount = 0,
    int PreviousSelectedCount = 0,
    int RetainedCount = 0,
    int EnteredCount = 0,
    int LeftCount = 0,
    int MissingPreviousCount = 0,
    int ActiveReplacedCount = 0,
    bool HasLocalVelocityEstimate = false,
    int TrackedPlayerVelocityCount = 0,
    double SmoothedLocalSpeed = 0,
    double MotionFactor = 0,
    long RetentionBonus = 0,
    IReadOnlyList<int>? CandidateRankHistogram = null,
    long SnapshotTicks = 0,
    long SelectorTicks = 0,
    long TotalTicks = 0,
    PlayerVisibilityAppliedSource AppliedSource = PlayerVisibilityAppliedSource.LegacyFallback,
    PlayerVisibilityFallbackReason FallbackReason = PlayerVisibilityFallbackReason.None,
    int ProposalSelectedCount = 0,
    int AppliedSelectedCount = 0,
    string? FailureReason = null
)
{
    public bool HasValue => Status != PlayerVisibilitySelectionStatus.None;
}

internal enum PlayerVisibilitySelectionStatus
{
    None,
    Ready,
    Warmup,
    Unavailable,
    Failed,
}
