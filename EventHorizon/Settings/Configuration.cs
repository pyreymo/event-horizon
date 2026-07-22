using System.Reflection;
using Dalamud.Configuration;
using Newtonsoft.Json;

namespace EventHorizon.Settings;

[Serializable]
internal class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool HideAllOtherPlayers { get; set; } = true;
    public bool EnableTemporaryShowAllPlayersShortcut { get; set; } = true;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<int> TemporarilyShowAllPlayersKeys { get; set; } = [0x11, 0x12];
    public bool DisableInDuty { get; set; } = true;
    public bool DisableCullingBelowPlayerCount { get; set; } = true;

    [ConfigRange(1, 100)]
    public int DisableCullingPlayerCountThreshold { get; set; } = 25;
    public bool LimitVisiblePlayerCount { get; set; }

    [ConfigRange(1, 100)]
    public int VisiblePlayerCountLimit { get; set; } = 30;
    public bool HideOtherPlayerCompanions { get; set; } = true;
    public bool HideOtherPlayerOrnaments { get; set; } = false;
    public bool HideOtherPlayerBattlePets { get; set; } = false;
    public bool HideUnattachedEventNpcs { get; set; } = false;
    public bool ShowDtrBar { get; set; } = true;
    public bool ShowFrameRateInDtrBar { get; set; } = true;
    public bool EnableDtrBackground { get; set; } = false;
    public bool EnableHiddenPlayerGroundMarker { get; set; } = true;
    public bool UseHiddenPlayerMarkerDot { get; set; } = false;
    public byte HiddenPlayerMarkerDotColorRed { get; set; } = 124;
    public byte HiddenPlayerMarkerDotColorGreen { get; set; } = 89;
    public byte HiddenPlayerMarkerDotColorBlue { get; set; } = 158;
    public byte HiddenPlayerMarkerDotColorAlpha { get; set; } = 180;

    [ConfigRange(1, 20)]
    public float HiddenPlayerMarkerDotRadius { get; set; } = 5f;
    public bool EnableTargetingMeMarker { get; set; } = true;
    public bool EnableTargetingMeNamePlateMarker { get; set; } = true;
    public bool EnableTargetingMeDotMarker { get; set; } = false;
    public bool EnableTargetingMeVfxMarker { get; set; } = false;
    public bool EnableTargetingMeMarkerCurrentTargetTest { get; set; } = false;
    public bool DisableTargetingMeMarkerVfxInDuty { get; set; } = true;

    [ConfigRange(-500, 500)]
    public float TargetingMeMarkerOffsetX { get; set; } = 0;

    [ConfigRange(-500, 500)]
    public float TargetingMeMarkerOffsetY { get; set; } = -45;

    [ConfigRange(0.1, 2)]
    public float TargetingMeMarkerScale { get; set; } = 1.33f;
    public byte TargetingMeMarkerOpacity { get; set; } = 255;
    public byte TargetingMeMarkerGlowOpacity { get; set; } = 255;
    public bool UseCustomTargetingMeMarkerColor { get; set; } = false;
    public byte TargetingMeMarkerColorRed { get; set; } = 255;
    public byte TargetingMeMarkerColorGreen { get; set; } = 120;
    public byte TargetingMeMarkerColorBlue { get; set; } = 40;
    public bool UseCustomTargetingMeDotColor { get; set; } = false;
    public byte TargetingMeDotColorRed { get; set; } = 255;
    public byte TargetingMeDotColorGreen { get; set; } = 0;
    public byte TargetingMeDotColorBlue { get; set; } = 0;
    public byte TargetingMeDotColorAlpha { get; set; } = 255;

    [ConfigRange(1, 20)]
    public float TargetingMeDotRadius { get; set; } = 5f;

    [ConfigRange(0, 80)]
    public float DtrBackgroundHorizontalPadding { get; set; } = 24f;

    [ConfigRange(0, 80)]
    public float DtrBackgroundPaddingTop { get; set; } = 10f;

    [ConfigRange(0, 80)]
    public float DtrBackgroundPaddingBottom { get; set; } = 4f;
    public byte DtrBackgroundAlpha { get; set; } = 128;
    public bool KeepFriends { get; set; } = true;
    public bool KeepPartyAndAllianceMembers { get; set; } = true;
    public bool KeepRecruitingPlayers { get; set; } = true;
    public bool KeepRecentChatPlayers { get; set; } = true;
    public bool KeepNearbyPlayers { get; set; }

    [ConfigRange(PlayerKeepRuleSettings.NearbyRangeMin, PlayerKeepRuleSettings.NearbyRangeMax)]
    public float KeepNearbyPlayersRange { get; set; } = 5f;
    public bool KeepTargetAndFocusPlayers { get; set; } = true;
    public bool KeepPlayersTargetingMe { get; set; } = true;
    public bool KeepSelectedRaces { get; set; }
    public bool HideBgParts { get; set; } = false;
    public bool HideTerrain { get; set; } = false;
    public bool HideWater { get; set; } = false;
    public bool HideGrass { get; set; } = false;
    public bool HideAll3DScene { get; set; } = false;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<PlayerKeepRuleId> KeepRuleOrder { get; set; } = PlayerKeepRuleOrder.CreateDefaultOrder();

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<PlayerKeepRuleId, PlayerKeepBudgetPolicy> KeepRuleBudgetPolicies { get; set; } = PlayerKeepRulePolicies.Create();

    public HashSet<byte> KeptRaceSex { get; set; } = [];

    public static Configuration CreateSafeDefault() =>
        new()
        {
            HideAllOtherPlayers = false,
            EnableDtrBackground = false,
            EnableHiddenPlayerGroundMarker = false,
            EnableTargetingMeMarker = false,
            HideBgParts = false,
            HideTerrain = false,
            HideWater = false,
            HideGrass = false,
            HideAll3DScene = false,
        };

    public bool Normalize(Func<int, bool> isValidVirtualKey)
    {
        var defaults = new Configuration();
        var changed = false;

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
