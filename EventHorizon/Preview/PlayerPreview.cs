using System;
using System.Collections.Generic;
using System.Numerics;
using EventHorizon.Culling;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Preview;

internal sealed unsafe class PlayerPreview(Configuration configuration)
{
    private const int SelectionVisibilityLeaseMs = 500;

    private PlayerPreviewSnapshot snapshot = PlayerPreviewSnapshot.Empty(PlayerPreviewEmptyReason.PlayerUnavailable);
    private uint? selectedPlayerEntityId;
    private long selectionExpiresAt;

    public PlayerPreviewSnapshot Snapshot => snapshot;

    public uint? ActiveSelectedPlayerEntityId
    {
        get
        {
            if (!selectedPlayerEntityId.HasValue)
            {
                return null;
            }

            if (Environment.TickCount64 <= selectionExpiresAt)
            {
                return selectedPlayerEntityId;
            }

            selectedPlayerEntityId = null;
            selectionExpiresAt = 0;
            return null;
        }
    }

    public bool SetSelectedPlayer(uint? entityId)
    {
        var previousEntityId = ActiveSelectedPlayerEntityId;
        if (entityId.HasValue)
        {
            selectedPlayerEntityId = entityId.Value;
            selectionExpiresAt = Environment.TickCount64 + SelectionVisibilityLeaseMs;
        }
        else
        {
            selectedPlayerEntityId = null;
            selectionExpiresAt = 0;
        }

        return previousEntityId != ActiveSelectedPlayerEntityId;
    }

    public void Refresh(GameObjectManager* manager, PlayerAdmissionDecision[] targets)
    {
        var builder = PlayerPreviewBuilder.Begin(manager, configuration);
        foreach (var target in targets)
        {
            if (!CharacterObjectSlots.IsEvenSlot(target.ObjectIndex))
            {
                continue;
            }

            var gameObject = target.Resolve(manager);
            if (gameObject != null)
            {
                builder.Add(gameObject, target.ObjectIndex, GetName(gameObject), target.Decision, !target.Allowed, target.CutByBudget);
            }
        }

        snapshot = builder.Build();
    }

    public void Clear(PlayerPreviewEmptyReason reason)
    {
        snapshot = PlayerPreviewSnapshot.Empty(reason);
        selectedPlayerEntityId = null;
        selectionExpiresAt = 0;
    }

    private static string GetName(GameObject* gameObject)
    {
        var name = gameObject->NameString;
        return string.IsNullOrWhiteSpace(name) ? $"#{gameObject->EntityId:X8}" : name;
    }

}

internal sealed record PlayerPreviewSnapshot(
    float ViewRange,
    float NearbyRange,
    IReadOnlyList<PlayerPreviewEntry> Players,
    PlayerPreviewStats Stats,
    PlayerPreviewEmptyReason EmptyReason
)
{
    public static PlayerPreviewSnapshot Empty(PlayerPreviewEmptyReason reason) => new(50f, 0f, [], PlayerPreviewStats.Empty, reason);
}

internal enum PlayerPreviewEmptyReason
{
    None,
    PlayerHidingDisabled,
    TemporaryReveal,
    PlayerUnavailable,
    SuspendedInDuty,
    SuspendedByLowPlayerCount,
    NativeHookFailed,
    NoOtherPlayers,
}

internal readonly record struct PlayerPreviewEntry(
    uint EntityId,
    int ObjectIndex,
    string Name,
    Vector2 RelativeXZ,
    float Distance,
    bool IsVisible,
    PlayerKeepRuleId? BestRule,
    PlayerKeepBudgetPolicy BudgetPolicy,
    int? BudgetRank,
    bool CutByBudget,
    PlayerKeepRuleMask MatchedRules
);

internal readonly record struct PlayerPreviewStats(int TotalPlayers, int VisiblePlayers, int HiddenPlayers, int BudgetLimit)
{
    public static readonly PlayerPreviewStats Empty = new(0, 0, 0, 0);
}

internal sealed unsafe class PlayerPreviewBuilder
{
    private const float DefaultViewRange = 50f;
    private const float DisabledNearbyRange = 0f;

    private readonly List<PlayerPreviewEntry> players = [];
    private readonly Vector3 localPlayerPosition;
    private readonly float viewRange;
    private readonly float nearbyRange;
    private readonly int budgetLimit;
    private int visiblePlayers;
    private int hiddenPlayers;

    private PlayerPreviewBuilder(Vector3 localPlayerPosition, float viewRange, float nearbyRange, int budgetLimit)
    {
        this.localPlayerPosition = localPlayerPosition;
        this.viewRange = viewRange;
        this.nearbyRange = nearbyRange;
        this.budgetLimit = budgetLimit;
    }

    public static PlayerPreviewBuilder Begin(GameObjectManager* manager, Configuration configuration)
    {
        return new PlayerPreviewBuilder(
            GetLocalPlayerPosition(manager),
            DefaultViewRange,
            configuration.KeepNearbyPlayers
                ? Math.Clamp(
                    configuration.KeepNearbyPlayersRange,
                    PlayerKeepRuleSettings.NearbyRangeMin,
                    PlayerKeepRuleSettings.NearbyRangeMax
                )
                : DisabledNearbyRange,
            Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 100)
        );
    }

    public void Add(
        GameObject* gameObject,
        int objectIndex,
        string name,
        PlayerKeepDecision keepDecision,
        bool shouldHide,
        bool cutByBudget
    )
    {
        if (gameObject == null)
        {
            return;
        }

        var (relativeXz, distance) = GetRelativePosition(gameObject);

        if (shouldHide)
        {
            hiddenPlayers++;
        }
        else
        {
            visiblePlayers++;
        }

        players.Add(
            new PlayerPreviewEntry(
                gameObject->EntityId,
                objectIndex,
                name,
                relativeXz,
                distance,
                !shouldHide,
                keepDecision.RuleId,
                keepDecision.BudgetPolicy,
                keepDecision.HasMatchingRule ? keepDecision.Rank : null,
                cutByBudget,
                keepDecision.MatchedRules
            )
        );
    }

    public PlayerPreviewSnapshot Build()
    {
        return new PlayerPreviewSnapshot(
            viewRange,
            nearbyRange,
            [.. players],
            new PlayerPreviewStats(players.Count, visiblePlayers, hiddenPlayers, budgetLimit),
            players.Count == 0 ? PlayerPreviewEmptyReason.NoOtherPlayers : PlayerPreviewEmptyReason.None
        );
    }

    private static Vector3 GetLocalPlayerPosition(GameObjectManager* manager)
    {
        if (manager == null)
        {
            return Vector3.Zero;
        }

        var localPlayer = manager->Objects.IndexSorted[0].Value;
        return localPlayer != null ? localPlayer->Position : Vector3.Zero;
    }

    private (Vector2 RelativeXZ, float Distance) GetRelativePosition(GameObject* gameObject)
    {
        var relativeXz = new Vector2(gameObject->Position.X - localPlayerPosition.X, gameObject->Position.Z - localPlayerPosition.Z);
        return (relativeXz, relativeXz.Length());
    }

}
