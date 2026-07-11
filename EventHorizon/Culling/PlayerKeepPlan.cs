using System;
using System.Collections.Generic;
using EventHorizon.Settings;

namespace EventHorizon.Culling;

[Flags]
internal enum PlayerKeepRuleMask
{
    None = 0,
    TargetFocus = 1 << 0,
    PartyAlliance = 1 << 1,
    Friends = 1 << 2,
    TargetingMe = 1 << 3,
    RecentChat = 1 << 4,
    Recruiting = 1 << 5,
    Nearby = 1 << 6,
    Race = 1 << 7,
}

internal static class RaceSexFilter
{
    public const byte MinRace = 1;
    public const byte MaxRace = 8;
    public const byte MaleSex = 0;
    public const byte FemaleSex = 1;

    public static byte Pack(byte race, byte sex)
    {
        return (byte)(race | (sex << 4));
    }
}

internal sealed class PlayerKeepPlan
{
    private readonly Dictionary<nint, PlayerKeepDecision> keepDecisions = [];
    private readonly HashSet<nint> visibleBudgetedPlayers = [];
    private readonly List<PlayerKeepCandidate> budgetedPlayers = [];
    private bool limitVisibleBudgetedPlayers;

    public void Update(Configuration configuration, IReadOnlyList<PlayerKeepCandidate> candidates)
    {
        keepDecisions.Clear();
        foreach (var candidate in candidates)
        {
            keepDecisions[candidate.Address] = candidate.KeepDecision;
        }

        UpdateVisibleBudgetedPlayers(configuration, candidates);
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
