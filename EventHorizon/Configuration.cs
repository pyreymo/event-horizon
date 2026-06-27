using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Newtonsoft.Json;

namespace EventHorizon;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool HideAllOtherPlayers { get; set; } = true;
    public bool DisableInDuty { get; set; } = true;
    public bool DisableCullingBelowPlayerCount { get; set; } = true;
    public int DisableCullingPlayerCountThreshold { get; set; } = 25;
    public bool LimitVisiblePlayerCount { get; set; }
    public int VisiblePlayerCountLimit { get; set; } = 30;
    public bool HideOtherPlayerCompanions { get; set; } = true;
    public bool HideOtherPlayerOrnaments { get; set; } = false;
    public bool ShowDtrBar { get; set; } = true;
    public bool EnableFadeTransitions { get; set; } = true;
    public bool KeepFriends { get; set; } = true;
    public bool KeepPartyAndAllianceMembers { get; set; } = true;
    public bool KeepRecruitingPlayers { get; set; } = true;
    public bool KeepRecentChatPlayers { get; set; } = true;
    public bool KeepNearbyPlayers { get; set; }
    public float KeepNearbyPlayersRange { get; set; } = 5f;
    public bool KeepTargetAndFocusPlayers { get; set; } = true;
    public bool KeepPlayersTargetingMe { get; set; } = true;
    public bool KeepSelectedRaces { get; set; }

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<PlayerKeepRuleId> KeepRuleOrder { get; set; } = PlayerKeepRuleOrder.CreateDefaultOrder();

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<PlayerKeepRuleId, PlayerKeepBudgetPolicy> KeepRuleBudgetPolicies { get; set; } =
        PlayerKeepRuleBudgetDefaults.Create();

    public HashSet<byte> KeptRaceSex { get; set; } = [];

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

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

internal static class PlayerKeepRuleBudgetDefaults
{
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

        var defaultPolicies = Create();
        return defaultPolicies[ruleId];
    }

    public static void SetPolicy(Configuration configuration, PlayerKeepRuleId ruleId, PlayerKeepBudgetPolicy policy)
    {
        configuration.KeepRuleBudgetPolicies ??= [];
        configuration.KeepRuleBudgetPolicies[ruleId] = policy;
    }
}

internal static class PlayerKeepRuleOrder
{
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

    public static void Reset(Configuration configuration)
    {
        configuration.KeepRuleOrder = CreateDefaultOrder();
    }
}
