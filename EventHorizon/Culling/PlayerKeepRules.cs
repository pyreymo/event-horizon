using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class PlayerKeepRules(Configuration configuration, IObjectTable objectTable, ITargetManager targetManager)
{
    private const int RuleCount = 8;
    private const int TargetingMePlayerKeepMs = 60_000;
    private const int RecentTargetPlayerKeepMs = 30_000;
    private const int RecentChatPlayerKeepMs = 300_000;
    private const byte RecruitingOnlineStatusId = 26;

    private readonly Configuration configuration = configuration;
    private readonly IObjectTable objectTable = objectTable;
    private readonly ITargetManager targetManager = targetManager;
    private readonly HashSet<ulong> nearbyKeptPlayers = [];
    private readonly Dictionary<ulong, long> recentTargetPlayers = [];
    private readonly Dictionary<ulong, long> targetingMePlayers = [];
    private readonly Dictionary<string, long> recentChatPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ulong> expiredPlayerIds = [];
    private readonly List<string> expiredPlayerNames = [];
    private readonly int[] ruleRanks = new int[RuleCount];
    private readonly PlayerKeepBudgetPolicy[] ruleBudgetPolicies = new PlayerKeepBudgetPolicy[RuleCount];
    private readonly Lock recentChatPlayersLock = new();
    private Vector3 localPlayerPosition;
    private ulong localPlayerId;
    private nint targetAddress;
    private nint focusTargetAddress;
    private long updateNow;
    private float nearbyRangeSq;
    private bool hasRecentChatPlayers;

    #region Lifecycle

    public void BeforeUpdate()
    {
        updateNow = Environment.TickCount64;
        PlayerKeepRuleOrder.FillRanks(configuration, ruleRanks);
        PlayerKeepRulePolicies.FillPolicies(configuration, ruleBudgetPolicies);
        RefreshObjectState();
        PruneExpiredKeepState();
    }

    public void Clear()
    {
        nearbyKeptPlayers.Clear();
        recentTargetPlayers.Clear();
        targetingMePlayers.Clear();
        ClearRecentChatPlayers();
    }

    public void RecordChatMessage(IChatMessage message)
    {
        if (!configuration.KeepRecentChatPlayers || !IsPlayerChatOrEmote(message.LogKind))
        {
            return;
        }

        var playerNames = GetPlayerNamesFromChatMessage(message);
        if (playerNames.Count == 0)
        {
            return;
        }

        var expireTime = Environment.TickCount64 + RecentChatPlayerKeepMs;
        lock (recentChatPlayersLock)
        {
            foreach (var playerName in playerNames)
            {
                recentChatPlayers[playerName] = expireTime;
            }
        }
    }

    #endregion

    #region Rules

    public PlayerKeepDecision GetKeepDecision(GameObject* gameObject)
    {
        if (!IsPlayerObject(gameObject))
        {
            return PlayerKeepDecision.None;
        }

        PlayerKeepRuleId? winningRule = null;
        int? rank = null;
        var tieBreaker = PlayerKeepTieBreaker.None;
        var matchedRules = PlayerKeepRuleMask.None;
        if (ShouldKeepTargetOrFocusPlayer(gameObject))
        {
            matchedRules |= PlayerKeepRuleMask.TargetFocus;
            KeepBetterRule(ref winningRule, ref rank, ref tieBreaker, PlayerKeepRuleId.TargetFocus);
        }

        if (ShouldKeepPartyOrAllianceMember(gameObject))
        {
            matchedRules |= PlayerKeepRuleMask.PartyAlliance;
            KeepBetterRule(ref winningRule, ref rank, ref tieBreaker, PlayerKeepRuleId.PartyAlliance);
        }

        if (ShouldKeepFriend(gameObject))
        {
            matchedRules |= PlayerKeepRuleMask.Friends;
            KeepBetterRule(ref winningRule, ref rank, ref tieBreaker, PlayerKeepRuleId.Friends);
        }

        if (ShouldKeepPlayerTargetingLocalPlayer(gameObject))
        {
            matchedRules |= PlayerKeepRuleMask.TargetingMe;
            KeepBetterRule(ref winningRule, ref rank, ref tieBreaker, PlayerKeepRuleId.TargetingMe);
        }

        if (ShouldKeepRecentChatPlayer(gameObject))
        {
            matchedRules |= PlayerKeepRuleMask.RecentChat;
            KeepBetterRule(ref winningRule, ref rank, ref tieBreaker, PlayerKeepRuleId.RecentChat);
        }

        if (ShouldKeepRecruitingPlayer(gameObject))
        {
            matchedRules |= PlayerKeepRuleMask.Recruiting;
            KeepBetterRule(ref winningRule, ref rank, ref tieBreaker, PlayerKeepRuleId.Recruiting);
        }

        if (ShouldKeepNearbyPlayer(gameObject, out var nearbyDistanceSq))
        {
            matchedRules |= PlayerKeepRuleMask.Nearby;
            KeepBetterRule(
                ref winningRule,
                ref rank,
                ref tieBreaker,
                PlayerKeepRuleId.Nearby,
                PlayerKeepTieBreaker.Nearby(nearbyDistanceSq)
            );
        }

        if (ShouldKeepByRace(gameObject))
        {
            matchedRules |= PlayerKeepRuleMask.Race;
            KeepBetterRule(ref winningRule, ref rank, ref tieBreaker, PlayerKeepRuleId.Race);
        }

        if (!winningRule.HasValue || !rank.HasValue)
        {
            return PlayerKeepDecision.None;
        }

        return CreateKeepDecision(winningRule.Value, rank.Value, tieBreaker, matchedRules);
    }

    private bool ShouldKeepFriend(GameObject* gameObject)
    {
        if (!configuration.KeepFriends)
        {
            return false;
        }

        return ((BattleChara*)gameObject)->IsFriend;
    }

    private bool ShouldKeepPartyOrAllianceMember(GameObject* gameObject)
    {
        if (!configuration.KeepPartyAndAllianceMembers)
        {
            return false;
        }

        var player = (BattleChara*)gameObject;
        return player->IsPartyMember || player->IsAllianceMember;
    }

    private bool ShouldKeepRecruitingPlayer(GameObject* gameObject)
    {
        if (!configuration.KeepRecruitingPlayers)
        {
            return false;
        }

        return ((BattleChara*)gameObject)->OnlineStatus == RecruitingOnlineStatusId;
    }

    private bool ShouldKeepRecentChatPlayer(GameObject* gameObject)
    {
        if (!configuration.KeepRecentChatPlayers)
        {
            return false;
        }

        if (!hasRecentChatPlayers)
        {
            return false;
        }

        var playerName = NormalizePlayerName(gameObject->NameString);
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return false;
        }

        lock (recentChatPlayersLock)
        {
            if (!recentChatPlayers.TryGetValue(playerName, out var expireTime))
            {
                return false;
            }

            if (expireTime > updateNow)
            {
                return true;
            }

            recentChatPlayers.Remove(playerName);
            return false;
        }
    }

    private bool ShouldKeepNearbyPlayer(GameObject* gameObject, out float distanceSq)
    {
        distanceSq = float.MaxValue;

        if (!configuration.KeepNearbyPlayers)
        {
            return false;
        }

        if (localPlayerId == 0)
        {
            return false;
        }

        var player = (BattleChara*)gameObject;
        var playerId = GetPlayerTrackingId(player);
        if (playerId == 0)
        {
            return false;
        }

        distanceSq = Vector3.DistanceSquared(localPlayerPosition, player->Position);

        if (nearbyKeptPlayers.Contains(playerId))
        {
            if (distanceSq <= nearbyRangeSq)
            {
                return true;
            }

            nearbyKeptPlayers.Remove(playerId);
            return false;
        }

        if (distanceSq > nearbyRangeSq)
        {
            return false;
        }

        nearbyKeptPlayers.Add(playerId);
        return true;
    }

    private bool ShouldKeepTargetOrFocusPlayer(GameObject* gameObject)
    {
        if (!configuration.KeepTargetAndFocusPlayers)
        {
            return false;
        }

        var player = (BattleChara*)gameObject;
        var playerId = GetPlayerTrackingId(player);
        if (playerId == 0)
        {
            return false;
        }

        if (IsTargetOrFocus(gameObject))
        {
            recentTargetPlayers[playerId] = updateNow + RecentTargetPlayerKeepMs;
            return true;
        }

        return IsTimedKeepAlive(recentTargetPlayers, playerId, updateNow);
    }

    private bool ShouldKeepPlayerTargetingLocalPlayer(GameObject* gameObject)
    {
        if (!configuration.KeepPlayersTargetingMe)
        {
            return false;
        }

        var player = (BattleChara*)gameObject;
        var playerId = GetPlayerTrackingId(player);
        if (playerId == 0 || localPlayerId == 0)
        {
            return false;
        }

        if ((ulong)player->GetTargetId() == localPlayerId)
        {
            targetingMePlayers[playerId] = updateNow + TargetingMePlayerKeepMs;
            return true;
        }

        return IsTimedKeepAlive(targetingMePlayers, playerId, updateNow);
    }

    private bool ShouldKeepByRace(GameObject* gameObject)
    {
        if (!configuration.KeepSelectedRaces)
        {
            return false;
        }

        var player = (BattleChara*)gameObject;
        var customizeData = player->DrawData.CustomizeData;
        return configuration.KeptRaceSex.Contains(RaceSexFilter.Pack(customizeData.Race, customizeData.Sex));
    }

    #endregion

    #region State

    private void PruneExpiredKeepState()
    {
        PruneExpiredKeepState(recentTargetPlayers, updateNow, expiredPlayerIds);
        PruneExpiredKeepState(targetingMePlayers, updateNow, expiredPlayerIds);
        PruneExpiredRecentChatPlayers(updateNow);
    }

    private void RefreshObjectState()
    {
        var localPlayer = objectTable.LocalPlayer;
        localPlayerId = localPlayer?.GameObjectId ?? 0;
        localPlayerPosition = localPlayer?.Position ?? default;
        targetAddress = targetManager.Target?.Address ?? nint.Zero;
        focusTargetAddress = targetManager.FocusTarget?.Address ?? nint.Zero;

        var nearbyRange = Math.Clamp(
            configuration.KeepNearbyPlayersRange,
            PlayerKeepRuleSettings.NearbyRangeMin,
            PlayerKeepRuleSettings.NearbyRangeMax
        );
        nearbyRangeSq = nearbyRange * nearbyRange;

        lock (recentChatPlayersLock)
        {
            hasRecentChatPlayers = recentChatPlayers.Count > 0;
        }
    }

    private static bool IsTimedKeepAlive(Dictionary<ulong, long> keepAlivePlayers, ulong playerId, long now)
    {
        if (!keepAlivePlayers.TryGetValue(playerId, out var expireTime))
        {
            return false;
        }

        if (expireTime > now)
        {
            return true;
        }

        keepAlivePlayers.Remove(playerId);
        return false;
    }

    private static void PruneExpiredKeepState(Dictionary<ulong, long> keepAlivePlayers, long now, List<ulong> expiredIds)
    {
        if (keepAlivePlayers.Count == 0)
        {
            return;
        }

        expiredIds.Clear();
        foreach (var (playerId, expireTime) in keepAlivePlayers)
        {
            if (expireTime <= now)
            {
                expiredIds.Add(playerId);
            }
        }

        foreach (var playerId in expiredIds)
        {
            keepAlivePlayers.Remove(playerId);
        }

        expiredIds.Clear();
    }

    private void PruneExpiredRecentChatPlayers(long now)
    {
        lock (recentChatPlayersLock)
        {
            if (recentChatPlayers.Count == 0)
            {
                return;
            }

            expiredPlayerNames.Clear();
            foreach (var (playerName, expireTime) in recentChatPlayers)
            {
                if (expireTime <= now)
                {
                    expiredPlayerNames.Add(playerName);
                }
            }

            foreach (var playerName in expiredPlayerNames)
            {
                recentChatPlayers.Remove(playerName);
            }

            expiredPlayerNames.Clear();
        }
    }

    #endregion

    #region Object Helpers

    private static HashSet<string> GetPlayerNamesFromChatMessage(IChatMessage message)
    {
        var playerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddPlayerPayloadNames(playerNames, message.Sender);
        AddPlayerPayloadNames(playerNames, message.Message);
        AddRecentChatPlayerName(playerNames, message.Sender.TextValue);

        return playerNames;
    }

    private static void AddPlayerPayloadNames(HashSet<string> playerNames, SeString text)
    {
        foreach (var payload in text.Payloads)
        {
            if (payload is PlayerPayload playerPayload)
            {
                AddRecentChatPlayerName(playerNames, playerPayload.PlayerName);
            }
        }
    }

    private static void AddRecentChatPlayerName(HashSet<string> playerNames, string playerName)
    {
        var normalizedName = NormalizePlayerName(playerName);
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            playerNames.Add(normalizedName);
        }
    }

    private static bool IsPlayerChatOrEmote(XivChatType chatType) =>
        chatType
            is XivChatType.Say
                or XivChatType.Yell
                or XivChatType.Shout
                or XivChatType.TellIncoming
                or XivChatType.Party
                or XivChatType.CrossParty
                or XivChatType.Alliance
                or XivChatType.FreeCompany
                or XivChatType.PvPTeam
                or XivChatType.NoviceNetwork
                or XivChatType.CrossLinkShell1
                or XivChatType.CrossLinkShell2
                or XivChatType.CrossLinkShell3
                or XivChatType.CrossLinkShell4
                or XivChatType.CrossLinkShell5
                or XivChatType.CrossLinkShell6
                or XivChatType.CrossLinkShell7
                or XivChatType.CrossLinkShell8
                or XivChatType.Ls1
                or XivChatType.Ls2
                or XivChatType.Ls3
                or XivChatType.Ls4
                or XivChatType.Ls5
                or XivChatType.Ls6
                or XivChatType.Ls7
                or XivChatType.Ls8
        || chatType.ToString().Contains("Emote", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return string.Empty;
        }

        return string.Join(' ', playerName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private bool IsTargetOrFocus(GameObject* gameObject)
    {
        var address = (nint)gameObject;
        return address == targetAddress || address == focusTargetAddress;
    }

    private static ulong GetPlayerTrackingId(BattleChara* player)
    {
        return player == null ? 0 : (ulong)((GameObject*)player)->GetGameObjectId();
    }

    private static bool IsPlayerObject(GameObject* gameObject) => gameObject->ObjectKind == ObjectKind.Pc;

    private PlayerKeepDecision CreateKeepDecision(
        PlayerKeepRuleId ruleId,
        int rank,
        PlayerKeepTieBreaker tieBreaker,
        PlayerKeepRuleMask matchedRules
    ) => PlayerKeepDecision.Keep(ruleId, rank, ruleBudgetPolicies[(int)ruleId], tieBreaker, matchedRules);

    private void KeepBetterRule(
        ref PlayerKeepRuleId? winningRule,
        ref int? currentRank,
        ref PlayerKeepTieBreaker currentTieBreaker,
        PlayerKeepRuleId rule,
        PlayerKeepTieBreaker tieBreaker
    )
    {
        var rank = ruleRanks[(int)rule];
        if (!currentRank.HasValue || rank < currentRank.Value)
        {
            winningRule = rule;
            currentRank = rank;
            currentTieBreaker = tieBreaker;
        }
    }

    private void KeepBetterRule(
        ref PlayerKeepRuleId? winningRule,
        ref int? currentRank,
        ref PlayerKeepTieBreaker currentTieBreaker,
        PlayerKeepRuleId rule
    ) => KeepBetterRule(ref winningRule, ref currentRank, ref currentTieBreaker, rule, PlayerKeepTieBreaker.None);

    #endregion

    #region Chat State

    private void ClearRecentChatPlayers()
    {
        lock (recentChatPlayersLock)
        {
            recentChatPlayers.Clear();
        }
    }

    #endregion
}
