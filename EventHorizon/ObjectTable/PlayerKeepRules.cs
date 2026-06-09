using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.ObjectTable;

internal sealed unsafe class PlayerKeepRules(
    Configuration configuration,
    IObjectTable objectTable,
    ITargetManager targetManager
)
{
    private const int TargetingMePlayerKeepMs = 60_000;
    private const int RecentTargetPlayerKeepMs = 30_000;
    private const int RecentChatPlayerKeepMs = 300_000;
    private const byte RecruitingOnlineStatusId = 26;

    private readonly Configuration configuration = configuration;
    private readonly IObjectTable objectTable = objectTable;
    private readonly ITargetManager targetManager = targetManager;
    private readonly object recentChatPlayersLock = new();
    private readonly HashSet<ulong> nearbyKeptPlayers = [];
    private readonly Dictionary<ulong, long> recentTargetPlayers = [];
    private readonly Dictionary<ulong, long> targetingMePlayers = [];
    private readonly Dictionary<string, long> recentChatPlayers = new(
        StringComparer.OrdinalIgnoreCase
    );

    public bool NeedsDynamicRefresh =>
        configuration.KeepRecruitingPlayers
        || configuration.KeepRecentChatPlayers
        || configuration.KeepNearbyPlayers
        || configuration.KeepTargetAndFocusPlayers
        || configuration.KeepPlayersTargetingMe;

    #region Lifecycle

    public void BeforeUpdate()
    {
        PruneExpiredKeepState();
    }

    public void Clear()
    {
        nearbyKeptPlayers.Clear();
        recentTargetPlayers.Clear();
        targetingMePlayers.Clear();
        ClearRecentChatPlayers();
    }

    public void RecordChatMessage(IHandleableChatMessage message)
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

    public bool ShouldKeep(GameObject* gameObject)
    {
        return IsPlayerObject(gameObject)
            && (
                ShouldKeepFriend(gameObject)
                || ShouldKeepPartyOrAllianceMember(gameObject)
                || ShouldKeepRecruitingPlayer(gameObject)
                || ShouldKeepRecentChatPlayer(gameObject)
                || ShouldKeepTargetOrFocusPlayer(gameObject)
                || ShouldKeepPlayerTargetingLocalPlayer(gameObject)
                || ShouldKeepNearbyPlayer(gameObject)
                || ShouldKeepByRace(gameObject)
            );
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

            if (expireTime > Environment.TickCount64)
            {
                return true;
            }

            recentChatPlayers.Remove(playerName);
            return false;
        }
    }

    private bool ShouldKeepNearbyPlayer(GameObject* gameObject)
    {
        if (!configuration.KeepNearbyPlayers)
        {
            return false;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            return false;
        }

        var player = (BattleChara*)gameObject;
        var playerId = GetPlayerTrackingId(player);
        if (playerId == 0)
        {
            return false;
        }

        var range = Math.Clamp(configuration.KeepNearbyPlayersRange, 1f, 50f);
        var distanceSq = Vector3.DistanceSquared(localPlayer.Position, player->Position);

        if (nearbyKeptPlayers.Contains(playerId))
        {
            if (distanceSq <= range * range)
            {
                return true;
            }

            nearbyKeptPlayers.Remove(playerId);
            return false;
        }

        if (distanceSq > range * range)
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

        var now = Environment.TickCount64;
        if (IsTargetOrFocus(gameObject))
        {
            recentTargetPlayers[playerId] = now + RecentTargetPlayerKeepMs;
            return true;
        }

        return IsTimedKeepAlive(recentTargetPlayers, playerId, now);
    }

    private bool ShouldKeepPlayerTargetingLocalPlayer(GameObject* gameObject)
    {
        if (!configuration.KeepPlayersTargetingMe)
        {
            return false;
        }

        var player = (BattleChara*)gameObject;
        var playerId = GetPlayerTrackingId(player);
        var localPlayerId = objectTable.LocalPlayer?.GameObjectId ?? 0;
        if (playerId == 0 || localPlayerId == 0)
        {
            return false;
        }

        var now = Environment.TickCount64;
        if ((ulong)player->GetTargetId() == localPlayerId)
        {
            targetingMePlayers[playerId] = now + TargetingMePlayerKeepMs;
            return true;
        }

        return IsTimedKeepAlive(targetingMePlayers, playerId, now);
    }

    private bool ShouldKeepByRace(GameObject* gameObject)
    {
        if (!configuration.KeepSelectedRaces)
        {
            return false;
        }

        var player = (BattleChara*)gameObject;
        var customizeData = player->DrawData.CustomizeData;
        return configuration.KeptRaceSex.Contains(
            RaceSexFilter.Pack(customizeData.Race, customizeData.Sex)
        );
    }

    #endregion

    #region State

    private void PruneExpiredKeepState()
    {
        var now = Environment.TickCount64;
        PruneExpiredKeepState(recentTargetPlayers, now);
        PruneExpiredKeepState(targetingMePlayers, now);
        PruneExpiredRecentChatPlayers(now);
    }

    private static bool IsTimedKeepAlive(
        Dictionary<ulong, long> keepAlivePlayers,
        ulong playerId,
        long now
    )
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

    private static void PruneExpiredKeepState(Dictionary<ulong, long> keepAlivePlayers, long now)
    {
        foreach (
            var (playerId, expireTime) in new List<KeyValuePair<ulong, long>>(keepAlivePlayers)
        )
        {
            if (expireTime <= now)
            {
                keepAlivePlayers.Remove(playerId);
            }
        }
    }

    #endregion

    #region Object Helpers

    private static HashSet<string> GetPlayerNamesFromChatMessage(IHandleableChatMessage message)
    {
        var playerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddPlayerPayloadNames(playerNames, message.Sender);
        AddPlayerPayloadNames(playerNames, message.Message);
        AddRecentChatPlayerName(playerNames, message.Sender.TextValue);

        return playerNames;
    }

    private static void AddPlayerPayloadNames(HashSet<string> playerNames, SeString text)
    {
        foreach (var payload in text.Payloads.OfType<PlayerPayload>())
        {
            AddRecentChatPlayerName(playerNames, payload.PlayerName);
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

        return string.Join(
            ' ',
            playerName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        );
    }

    private bool IsTargetOrFocus(GameObject* gameObject)
    {
        var address = (nint)gameObject;
        return address == targetManager.Target?.Address
            || address == targetManager.FocusTarget?.Address;
    }

    private static ulong GetPlayerTrackingId(BattleChara* player)
    {
        return player == null ? 0 : (ulong)((GameObject*)player)->GetGameObjectId();
    }

    private static bool IsPlayerObject(GameObject* gameObject) =>
        gameObject->ObjectKind == ObjectKind.Pc;

    #endregion

    #region Chat State

    private void PruneExpiredRecentChatPlayers(long now)
    {
        lock (recentChatPlayersLock)
        {
            foreach (
                var playerName in recentChatPlayers
                    .Where(x => x.Value <= now)
                    .Select(x => x.Key)
                    .ToArray()
            )
            {
                recentChatPlayers.Remove(playerName);
            }
        }
    }

    private void ClearRecentChatPlayers()
    {
        lock (recentChatPlayersLock)
        {
            recentChatPlayers.Clear();
        }
    }

    #endregion
}
