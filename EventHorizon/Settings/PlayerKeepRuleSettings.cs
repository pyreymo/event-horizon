using System;
using System.Collections.Generic;

namespace EventHorizon.Settings;

internal enum PlayerKeepBudgetPolicy
{
    Exempt,
    Counted,
}

internal enum PlayerKeepRuleId
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

internal static class PlayerKeepRuleSettings
{
    public const float NearbyRangeMin = 1f;
    public const float NearbyRangeMax = 50f;
}

internal static class PlayerKeepRulePolicies
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
