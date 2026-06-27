using System;
using System.Collections.Generic;

namespace EventHorizon.ObjectTable;

internal sealed class PlayerKeepPlan
{
    private readonly Dictionary<nint, PlayerKeepDecision> keepDecisions;
    private readonly HashSet<nint>? visibleBudgetedPlayers;

    private PlayerKeepPlan(Dictionary<nint, PlayerKeepDecision> keepDecisions, HashSet<nint>? visibleBudgetedPlayers)
    {
        this.keepDecisions = keepDecisions;
        this.visibleBudgetedPlayers = visibleBudgetedPlayers;
    }

    public int BudgetExemptPlayerCount { get; private init; }
    public int VisibleBudgetedPlayerCount { get; private init; }

    public static PlayerKeepPlan Build(Configuration configuration, IReadOnlyList<PlayerKeepCandidate> candidates)
    {
        var keepDecisions = new Dictionary<nint, PlayerKeepDecision>();
        foreach (var candidate in candidates)
        {
            keepDecisions[candidate.Address] = candidate.KeepDecision;
        }

        var visibleBudgetedPlayers = GetVisibleBudgetedPlayers(configuration, candidates);
        return new PlayerKeepPlan(keepDecisions, visibleBudgetedPlayers)
        {
            BudgetExemptPlayerCount = CountBudgetExemptPlayers(candidates),
            VisibleBudgetedPlayerCount = CountVisibleBudgetedPlayers(candidates, visibleBudgetedPlayers),
        };
    }

    public bool ShouldHide(nint address)
    {
        if (!keepDecisions.TryGetValue(address, out var keepDecision))
        {
            return true;
        }

        return keepDecision.Kind switch
        {
            PlayerKeepDecisionKind.Keep when keepDecision.BudgetPolicy == PlayerKeepBudgetPolicy.Exempt => false,
            PlayerKeepDecisionKind.Keep when keepDecision.BudgetPolicy == PlayerKeepBudgetPolicy.Counted =>
                visibleBudgetedPlayers?.Contains(address) == false,
            _ => true,
        };
    }

    private static HashSet<nint>? GetVisibleBudgetedPlayers(Configuration configuration, IReadOnlyList<PlayerKeepCandidate> candidates)
    {
        if (!configuration.LimitVisiblePlayerCount)
        {
            return null;
        }

        var budgetedPlayers = new List<PlayerKeepCandidate>();
        foreach (var candidate in candidates)
        {
            if (candidate.KeepDecision.BudgetPolicy == PlayerKeepBudgetPolicy.Counted)
            {
                budgetedPlayers.Add(candidate);
            }
        }

        budgetedPlayers.Sort(CompareBudgetedPlayers);

        var visiblePlayerLimit = Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 100);
        var visiblePlayers = new HashSet<nint>();
        for (var i = 0; i < budgetedPlayers.Count && i < visiblePlayerLimit; i++)
        {
            visiblePlayers.Add(budgetedPlayers[i].Address);
        }

        return visiblePlayers;
    }

    private static int CountBudgetExemptPlayers(IReadOnlyList<PlayerKeepCandidate> candidates)
    {
        var count = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.KeepDecision.BudgetPolicy == PlayerKeepBudgetPolicy.Exempt)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountVisibleBudgetedPlayers(IReadOnlyList<PlayerKeepCandidate> candidates, HashSet<nint>? visibleBudgetedPlayers)
    {
        if (visibleBudgetedPlayers != null)
        {
            return visibleBudgetedPlayers.Count;
        }

        var count = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.KeepDecision.BudgetPolicy == PlayerKeepBudgetPolicy.Counted)
            {
                count++;
            }
        }

        return count;
    }

    private static int CompareBudgetedPlayers(PlayerKeepCandidate left, PlayerKeepCandidate right)
    {
        var rankComparison = left.KeepDecision.Rank.CompareTo(right.KeepDecision.Rank);
        if (rankComparison != 0)
        {
            return rankComparison;
        }

        var distanceComparison = left.KeepDecision.TieBreaker.DistanceSq.CompareTo(right.KeepDecision.TieBreaker.DistanceSq);
        if (distanceComparison != 0)
        {
            return distanceComparison;
        }

        var entityComparison = left.EntityId.CompareTo(right.EntityId);
        return entityComparison != 0 ? entityComparison : left.Address.ToInt64().CompareTo(right.Address.ToInt64());
    }
}

internal readonly record struct PlayerKeepBudgetStats(
    int BudgetExemptPlayerCount,
    int VisibleBudgetedPlayerCount,
    int VisibleBudgetedPlayerLimit
);

internal readonly record struct PlayerKeepCandidate(nint Address, PlayerKeepDecision KeepDecision, uint EntityId);

internal enum PlayerKeepDecisionKind
{
    None,
    Keep,
}

internal readonly record struct PlayerKeepTieBreaker(float DistanceSq)
{
    public static readonly PlayerKeepTieBreaker None = new(float.MaxValue);

    public static PlayerKeepTieBreaker Nearby(float distanceSq) => new(distanceSq);
}

internal readonly record struct PlayerKeepDecision(
    PlayerKeepDecisionKind Kind,
    PlayerKeepRuleId? RuleId,
    int Rank,
    PlayerKeepBudgetPolicy BudgetPolicy,
    PlayerKeepTieBreaker TieBreaker
)
{
    public static readonly PlayerKeepDecision None = new(
        PlayerKeepDecisionKind.None,
        null,
        int.MaxValue,
        PlayerKeepBudgetPolicy.Exempt,
        PlayerKeepTieBreaker.None
    );

    public static PlayerKeepDecision Keep(
        PlayerKeepRuleId ruleId,
        int rank,
        PlayerKeepBudgetPolicy budgetPolicy,
        PlayerKeepTieBreaker tieBreaker
    ) => new(PlayerKeepDecisionKind.Keep, ruleId, rank, budgetPolicy, tieBreaker);
}
