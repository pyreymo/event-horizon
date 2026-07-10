using System;
using System.Collections.Generic;
using EventHorizon.Settings;

namespace EventHorizon.Culling;

public enum PlayerKeepBudgetPolicy
{
    Exempt,
    Counted,
}

public enum PlayerKeepRuleId
{
    TargetFocus,
    PartyAlliance,
    Friends,
    TargetingMe,
    RecentChat,
    Recruiting,
    Nearby,
    Race,
}

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

internal static class PlayerKeepRuleBudgetDefaults
{
    private const int RuleCount = 8;

    public static Dictionary<PlayerKeepRuleId, PlayerKeepBudgetPolicy> Create() =>
        new()
        {
            [PlayerKeepRuleId.TargetFocus] = PlayerKeepBudgetPolicy.Exempt,
            [PlayerKeepRuleId.PartyAlliance] = PlayerKeepBudgetPolicy.Exempt,
            [PlayerKeepRuleId.Friends] = PlayerKeepBudgetPolicy.Exempt,
            [PlayerKeepRuleId.TargetingMe] = PlayerKeepBudgetPolicy.Counted,
            [PlayerKeepRuleId.RecentChat] = PlayerKeepBudgetPolicy.Counted,
            [PlayerKeepRuleId.Recruiting] = PlayerKeepBudgetPolicy.Counted,
            [PlayerKeepRuleId.Nearby] = PlayerKeepBudgetPolicy.Counted,
            [PlayerKeepRuleId.Race] = PlayerKeepBudgetPolicy.Counted,
        };

    public static PlayerKeepBudgetPolicy GetPolicy(Configuration configuration, PlayerKeepRuleId ruleId)
    {
        if (configuration.KeepRuleBudgetPolicies?.TryGetValue(ruleId, out var policy) == true)
        {
            return policy;
        }

        return GetDefaultPolicy(ruleId);
    }

    public static void FillPolicies(Configuration configuration, PlayerKeepBudgetPolicy[] policies)
    {
        if (policies.Length < RuleCount)
        {
            return;
        }

        for (var index = 0; index < RuleCount; index++)
        {
            policies[index] = GetDefaultPolicy((PlayerKeepRuleId)index);
        }

        if (configuration.KeepRuleBudgetPolicies == null)
        {
            return;
        }

        foreach (var (ruleId, policy) in configuration.KeepRuleBudgetPolicies)
        {
            var index = (int)ruleId;
            if ((uint)index < policies.Length)
            {
                policies[index] = policy;
            }
        }
    }

    private static PlayerKeepBudgetPolicy GetDefaultPolicy(PlayerKeepRuleId ruleId)
    {
        return ruleId switch
        {
            PlayerKeepRuleId.TargetFocus => PlayerKeepBudgetPolicy.Exempt,
            PlayerKeepRuleId.PartyAlliance => PlayerKeepBudgetPolicy.Exempt,
            PlayerKeepRuleId.Friends => PlayerKeepBudgetPolicy.Exempt,
            _ => PlayerKeepBudgetPolicy.Counted,
        };
    }

    public static void SetPolicy(Configuration configuration, PlayerKeepRuleId ruleId, PlayerKeepBudgetPolicy policy)
    {
        configuration.KeepRuleBudgetPolicies ??= [];
        configuration.KeepRuleBudgetPolicies[ruleId] = policy;
    }
}

internal static class PlayerKeepRuleOrder
{
    private const int RuleCount = 8;
    private static readonly PlayerKeepRuleId[] DefaultOrder =
    [
        PlayerKeepRuleId.TargetFocus,
        PlayerKeepRuleId.PartyAlliance,
        PlayerKeepRuleId.Friends,
        PlayerKeepRuleId.TargetingMe,
        PlayerKeepRuleId.Nearby,
        PlayerKeepRuleId.RecentChat,
        PlayerKeepRuleId.Race,
        PlayerKeepRuleId.Recruiting,
    ];

    public static List<PlayerKeepRuleId> CreateDefaultOrder() => [.. DefaultOrder];

    public static IReadOnlyList<PlayerKeepRuleId> GetEffectiveOrder(Configuration configuration)
    {
        var effectiveOrder = new List<PlayerKeepRuleId>();
        var seenRules = new HashSet<PlayerKeepRuleId>();
        var configuredOrder = configuration.KeepRuleOrder;

        if (configuredOrder != null)
        {
            foreach (var rule in configuredOrder)
            {
                if (Array.IndexOf(DefaultOrder, rule) < 0 || !seenRules.Add(rule))
                {
                    continue;
                }

                effectiveOrder.Add(rule);
            }
        }

        foreach (var rule in DefaultOrder)
        {
            if (seenRules.Add(rule))
            {
                effectiveOrder.Add(rule);
            }
        }

        return effectiveOrder;
    }

    public static int GetRank(Configuration configuration, PlayerKeepRuleId ruleId)
    {
        var effectiveOrder = GetEffectiveOrder(configuration);
        for (var index = 0; index < effectiveOrder.Count; index++)
        {
            if (effectiveOrder[index] == ruleId)
            {
                return index;
            }
        }

        return effectiveOrder.Count;
    }

    public static void FillRanks(Configuration configuration, int[] ranks)
    {
        if (ranks.Length < RuleCount)
        {
            return;
        }

        Array.Fill(ranks, RuleCount);
        var nextRank = 0;
        var configuredOrder = configuration.KeepRuleOrder;
        if (configuredOrder != null)
        {
            foreach (var rule in configuredOrder)
            {
                AddRank(ranks, rule, ref nextRank);
            }
        }

        foreach (var rule in DefaultOrder)
        {
            AddRank(ranks, rule, ref nextRank);
        }
    }

    private static void AddRank(int[] ranks, PlayerKeepRuleId rule, ref int nextRank)
    {
        if (Array.IndexOf(DefaultOrder, rule) < 0)
        {
            return;
        }

        var index = (int)rule;
        if ((uint)index >= ranks.Length || ranks[index] != RuleCount)
        {
            return;
        }

        ranks[index] = nextRank++;
    }

    public static void Reset(Configuration configuration)
    {
        configuration.KeepRuleOrder = CreateDefaultOrder();
    }
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
