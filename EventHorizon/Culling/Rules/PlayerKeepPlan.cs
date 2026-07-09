using System;
using System.Collections.Generic;
using EventHorizon.Settings;

namespace EventHorizon.Culling.Rules;

internal sealed class PlayerKeepPlan
{
    private readonly Dictionary<nint, PlayerKeepDecision> keepDecisions = [];
    private readonly HashSet<nint> visibleBudgetedPlayers = [];
    private readonly List<PlayerKeepCandidate> budgetedPlayers = [];
    private bool limitVisibleBudgetedPlayers;

    public int BudgetExemptPlayerCount { get; private set; }
    public int VisibleBudgetedPlayerCount { get; private set; }

    public void Update(Configuration configuration, IReadOnlyList<PlayerKeepCandidate> candidates)
    {
        keepDecisions.Clear();
        foreach (var candidate in candidates)
        {
            keepDecisions[candidate.Address] = candidate.KeepDecision;
        }

        UpdateVisibleBudgetedPlayers(configuration, candidates);
        BudgetExemptPlayerCount = CountBudgetExemptPlayers(candidates);
        VisibleBudgetedPlayerCount = CountVisibleBudgetedPlayers(candidates);
    }

    public PlayerKeepDecision GetDecision(nint address) => keepDecisions.GetValueOrDefault(address, PlayerKeepDecision.None);

    public bool IsCutByBudget(nint address)
    {
        var keepDecision = GetDecision(address);
        return keepDecision.Kind == PlayerKeepDecisionKind.Keep
            && keepDecision.BudgetPolicy == PlayerKeepBudgetPolicy.Counted
            && limitVisibleBudgetedPlayers
            && !visibleBudgetedPlayers.Contains(address);
    }

    private void UpdateVisibleBudgetedPlayers(Configuration configuration, IReadOnlyList<PlayerKeepCandidate> candidates)
    {
        visibleBudgetedPlayers.Clear();
        budgetedPlayers.Clear();
        limitVisibleBudgetedPlayers = configuration.LimitVisiblePlayerCount;
        if (!configuration.LimitVisiblePlayerCount)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.KeepDecision.BudgetPolicy == PlayerKeepBudgetPolicy.Counted)
            {
                budgetedPlayers.Add(candidate);
            }
        }

        budgetedPlayers.Sort(CompareBudgetedPlayers);

        var visiblePlayerLimit = Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 100);
        for (var i = 0; i < budgetedPlayers.Count && i < visiblePlayerLimit; i++)
        {
            visibleBudgetedPlayers.Add(budgetedPlayers[i].Address);
        }
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

    private int CountVisibleBudgetedPlayers(IReadOnlyList<PlayerKeepCandidate> candidates)
    {
        if (limitVisibleBudgetedPlayers)
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

        var tieBreakerComparison = PlayerKeepTieBreaker.Compare(left.KeepDecision.TieBreaker, right.KeepDecision.TieBreaker);
        if (tieBreakerComparison != 0)
        {
            return tieBreakerComparison;
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

internal readonly record struct PlayerKeepTieBreaker(bool InViewport, float DistanceSq)
{
    public static readonly PlayerKeepTieBreaker None = new(false, float.MaxValue);

    public static PlayerKeepTieBreaker Nearby(float distanceSq) => new(false, distanceSq);

    public PlayerKeepTieBreaker WithViewport(bool inViewport) => this with { InViewport = inViewport };

    public static int Compare(PlayerKeepTieBreaker left, PlayerKeepTieBreaker right)
    {
        if (left.InViewport != right.InViewport)
        {
            return left.InViewport ? -1 : 1;
        }

        return left.DistanceSq.CompareTo(right.DistanceSq);
    }
}

internal readonly record struct PlayerKeepDecision(
    PlayerKeepDecisionKind Kind,
    PlayerKeepRuleId? RuleId,
    int Rank,
    PlayerKeepBudgetPolicy BudgetPolicy,
    PlayerKeepTieBreaker TieBreaker,
    PlayerKeepRuleMask MatchedRules
)
{
    public static readonly PlayerKeepDecision None = new(
        PlayerKeepDecisionKind.None,
        null,
        int.MaxValue,
        PlayerKeepBudgetPolicy.Exempt,
        PlayerKeepTieBreaker.None,
        PlayerKeepRuleMask.None
    );

    public static PlayerKeepDecision Keep(
        PlayerKeepRuleId ruleId,
        int rank,
        PlayerKeepBudgetPolicy budgetPolicy,
        PlayerKeepTieBreaker tieBreaker,
        PlayerKeepRuleMask matchedRules
    ) => new(PlayerKeepDecisionKind.Keep, ruleId, rank, budgetPolicy, tieBreaker, matchedRules);

    public PlayerKeepDecision WithViewport(bool inViewport) => this with { TieBreaker = TieBreaker.WithViewport(inViewport) };
}
