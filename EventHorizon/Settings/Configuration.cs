using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EventHorizon.Settings;

[Serializable]
internal class Configuration : IPluginConfiguration
{
    [JsonExtensionData]
    public IDictionary<string, JToken>? LegacySettings { get; set; }

    public int Version { get; set; } = 0;

    public bool HideAllOtherPlayers { get; set; } = true;
    public bool EnableTemporaryShowAllPlayersShortcut { get; set; } = true;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<int> TemporarilyShowAllPlayersKeys { get; set; } = [0x11, 0x12];
    public bool DisableInDuty { get; set; } = true;
    public bool DisableCullingBelowPlayerCount { get; set; } = true;

    [ConfigRange(1, 100)]
    public int DisableCullingPlayerCountThreshold { get; set; } = 25;
    public bool LimitVisiblePlayerCount { get; set; } = true;

    [ConfigRange(0, 100)]
    public int VisiblePlayerCountLimit { get; set; } = 30;
    public bool HideOtherPlayerCompanions { get; set; } = true;
    public bool HideOtherPlayerOrnaments { get; set; } = false;
    public bool HideOtherPlayerBattlePets { get; set; } = false;
    public bool HideUnattachedEventNpcs { get; set; } = false;
    public bool KeepFriends { get; set; } = true;
    public bool KeepPartyAndAllianceMembers { get; set; } = true;
    public bool KeepRecruitingPlayers { get; set; } = true;
    public bool KeepRecentChatPlayers { get; set; } = true;
    public bool KeepNearbyPlayers { get; set; } = true;

    [ConfigRange(PlayerKeepRuleSettings.NearbyRangeMin, PlayerKeepRuleSettings.NearbyRangeMax)]
    public float KeepNearbyPlayersRange { get; set; } = 5f;
    public bool KeepTargetAndFocusPlayers { get; set; } = true;
    public bool KeepPlayersTargetingMe { get; set; } = true;
    public bool KeepSelectedRaces { get; set; }

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<PlayerKeepRuleId> KeepRuleOrder { get; set; } = PlayerKeepRuleOrder.CreateDefaultOrder();

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<PlayerKeepRuleId, PlayerKeepBudgetPolicy> KeepRuleBudgetPolicies { get; set; } = PlayerKeepRulePolicies.Create();

    public HashSet<byte> KeptRaceSex { get; set; } = [];

    public RuleTreatment GetTreatment(PlayerKeepRuleId rule)
    {
        var enabled = rule switch
        {
            PlayerKeepRuleId.TargetFocus => KeepTargetAndFocusPlayers,
            PlayerKeepRuleId.PartyAlliance => KeepPartyAndAllianceMembers,
            PlayerKeepRuleId.Friends => KeepFriends,
            PlayerKeepRuleId.TargetingMe => KeepPlayersTargetingMe,
            PlayerKeepRuleId.RecentChat => KeepRecentChatPlayers,
            PlayerKeepRuleId.Recruiting => KeepRecruitingPlayers,
            PlayerKeepRuleId.Nearby => KeepNearbyPlayers,
            PlayerKeepRuleId.Race => KeepSelectedRaces,
            _ => false,
        };
        return !enabled ? RuleTreatment.Ordinary
            : PlayerKeepRulePolicies.GetPolicy(this, rule) == PlayerKeepBudgetPolicy.Exempt ? RuleTreatment.Always
            : RuleTreatment.Prefer;
    }

    public void SetTreatment(PlayerKeepRuleId rule, RuleTreatment treatment)
    {
        var enabled = treatment != RuleTreatment.Ordinary;
        switch (rule)
        {
            case PlayerKeepRuleId.TargetFocus:
                KeepTargetAndFocusPlayers = enabled;
                break;
            case PlayerKeepRuleId.PartyAlliance:
                KeepPartyAndAllianceMembers = enabled;
                break;
            case PlayerKeepRuleId.Friends:
                KeepFriends = enabled;
                break;
            case PlayerKeepRuleId.TargetingMe:
                KeepPlayersTargetingMe = enabled;
                break;
            case PlayerKeepRuleId.RecentChat:
                KeepRecentChatPlayers = enabled;
                break;
            case PlayerKeepRuleId.Recruiting:
                KeepRecruitingPlayers = enabled;
                break;
            case PlayerKeepRuleId.Nearby:
                KeepNearbyPlayers = enabled;
                break;
            case PlayerKeepRuleId.Race:
                KeepSelectedRaces = enabled;
                break;
        }
        PlayerKeepRulePolicies.SetPolicy(
            this,
            rule,
            treatment == RuleTreatment.Always ? PlayerKeepBudgetPolicy.Exempt : PlayerKeepBudgetPolicy.Counted
        );
    }

    public static Configuration CreateSafeDefault() => new() { HideAllOtherPlayers = false };

    public bool Normalize(Func<int, bool> isValidVirtualKey)
    {
        var defaults = new Configuration();
        var changed = Version < 1;
        Version = Math.Max(Version, 1);

        foreach (var property in typeof(Configuration).GetProperties())
        {
            var range = property.GetCustomAttribute<ConfigRangeAttribute>();
            if (range == null || range.IsValid(property.GetValue(this)))
            {
                continue;
            }

            property.SetValue(this, property.GetValue(defaults));
            changed = true;
        }

        var normalizedKeys = (TemporarilyShowAllPlayersKeys ?? defaults.TemporarilyShowAllPlayersKeys)
            .Where(isValidVirtualKey)
            .Distinct()
            .Order()
            .ToList();
        if (TemporarilyShowAllPlayersKeys == null || !TemporarilyShowAllPlayersKeys.SequenceEqual(normalizedKeys))
        {
            TemporarilyShowAllPlayersKeys = normalizedKeys;
            changed = true;
        }

        var normalizedOrder = PlayerKeepRuleOrder.GetEffectiveOrder(this).ToList();
        if (KeepRuleOrder == null || !KeepRuleOrder.SequenceEqual(normalizedOrder))
        {
            KeepRuleOrder = normalizedOrder;
            changed = true;
        }

        var normalizedPolicies = PlayerKeepRulePolicies.Create();
        if (KeepRuleBudgetPolicies != null)
        {
            foreach (var (ruleId, policy) in KeepRuleBudgetPolicies)
            {
                if (Enum.IsDefined(ruleId) && Enum.IsDefined(policy))
                {
                    normalizedPolicies[ruleId] = policy;
                }
            }
        }

        if (
            KeepRuleBudgetPolicies == null
            || KeepRuleBudgetPolicies.Count != normalizedPolicies.Count
            || normalizedPolicies.Any(entry => !KeepRuleBudgetPolicies.TryGetValue(entry.Key, out var policy) || policy != entry.Value)
        )
        {
            KeepRuleBudgetPolicies = normalizedPolicies;
            changed = true;
        }

        var normalizedRaceSex = (KeptRaceSex ?? []).Where(IsKnownRaceSex).ToHashSet();
        if (KeptRaceSex == null || !KeptRaceSex.SetEquals(normalizedRaceSex))
        {
            KeptRaceSex = normalizedRaceSex;
            changed = true;
        }

        return changed;
    }

    public void Save()
    {
        try
        {
            Plugin.PluginInterface.SavePluginConfig(this);
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "Failed to save configuration.");
        }
    }

    private static bool IsKnownRaceSex(byte value)
    {
        var race = value & 0x0F;
        var sex = value >> 4;
        return race is >= 1 and <= 8 && sex <= 1;
    }
}

internal enum RuleTreatment
{
    Ordinary,
    Prefer,
    Always,
}
