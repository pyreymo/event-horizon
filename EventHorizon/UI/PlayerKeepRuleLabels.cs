using EventHorizon.Localization;
using EventHorizon.Settings;

namespace EventHorizon.UI;

internal static class PlayerKeepRuleLabels
{
    public static string GetLabel(PlayerKeepRuleId rule) =>
        rule switch
        {
            PlayerKeepRuleId.TargetFocus => Loc.Text("Config.KeepTargetAndFocusPlayers"),
            PlayerKeepRuleId.PartyAlliance => Loc.Text("Config.KeepPartyAndAllianceMembers"),
            PlayerKeepRuleId.Friends => Loc.Text("Config.KeepFriends"),
            PlayerKeepRuleId.TargetingMe => Loc.Text("Config.KeepPlayersTargetingMe"),
            PlayerKeepRuleId.RecentChat => Loc.Text("Config.KeepRecentChatPlayers"),
            PlayerKeepRuleId.Recruiting => Loc.Text("Config.KeepRecruitingPlayers"),
            PlayerKeepRuleId.Nearby => Loc.Text("Config.KeepNearbyPlayers"),
            PlayerKeepRuleId.Race => Loc.Text("Config.KeepRaceFilter"),
            _ => rule.ToString(),
        };

    public static string GetHelpText(PlayerKeepRuleId rule) =>
        rule switch
        {
            PlayerKeepRuleId.TargetFocus => Loc.Text("Config.KeepTargetAndFocusPlayers.Help"),
            PlayerKeepRuleId.TargetingMe => Loc.Text("Config.KeepPlayersTargetingMe.Help"),
            PlayerKeepRuleId.RecentChat => Loc.Text("Config.KeepRecentChatPlayers.Help"),
            _ => string.Empty,
        };
}
