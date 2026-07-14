using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using EventHorizon.Culling;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Preview;

internal sealed unsafe class PlayerPreview(Configuration configuration)
{
    private const int SelectionVisibilityLeaseMs = 500;

    private readonly Dictionary<ulong, string> names = [];
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

    public void Refresh(GameObjectManager* manager, PlayerVisibilityTarget[] targets)
    {
        var builder = PlayerPreviewBuilder.Begin(manager, configuration);
        foreach (var target in targets)
        {
            if (!PlayerObjectSlots.IsPlayer(target.ObjectIndex))
            {
                continue;
            }

            var gameObject = FindPlayerObject(manager, target.Identity, target.ObjectIndex);
            if (gameObject != null)
            {
                builder.Add(
                    gameObject,
                    target.ObjectIndex,
                    GetName(gameObject),
                    target.Decision,
                    !target.DesiredVisible,
                    target.CutByBudget
                );
            }
        }

        snapshot = builder.Build();
    }

    public void Clear(PlayerPreviewEmptyReason reason)
    {
        names.Clear();
        snapshot = PlayerPreviewSnapshot.Empty(reason);
        selectedPlayerEntityId = null;
        selectionExpiresAt = 0;
    }

    private string GetName(GameObject* gameObject)
    {
        var gameObjectId = (ulong)gameObject->GetGameObjectId();
        if (gameObjectId != 0 && names.TryGetValue(gameObjectId, out var name))
        {
            return name;
        }

        name = PlayerPreviewBuilder.GetObjectName(gameObject);
        if (gameObjectId != 0 && !(name.Length == 9 && name[0] == '#'))
        {
            names[gameObjectId] = name;
        }

        return name;
    }

    private static GameObject* FindPlayerObject(GameObjectManager* manager, PlayerObjectIdentity identity, int expectedIndex)
    {
        if (manager == null || identity.Address == nint.Zero)
        {
            return null;
        }

        if (expectedIndex >= 0 && expectedIndex < manager->Objects.IndexSorted.Length)
        {
            var expectedObject = manager->Objects.IndexSorted[expectedIndex].Value;
            if (expectedObject != null && expectedObject->ObjectKind == ObjectKind.Pc && identity.Matches(expectedObject))
            {
                return expectedObject;
            }
        }

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject != null && gameObject->ObjectKind == ObjectKind.Pc && identity.Matches(gameObject))
            {
                return gameObject;
            }
        }

        return null;
    }
}

internal sealed record PlayerPreviewSnapshot(
    int Version,
    long UpdatedAtTicks,
    float ViewRange,
    float NearbyRange,
    IReadOnlyList<PlayerPreviewEntry> Players,
    PlayerPreviewStats Stats,
    PlayerPreviewEmptyReason EmptyReason
)
{
    public static PlayerPreviewSnapshot Empty(PlayerPreviewEmptyReason reason) => new(0, 0, 50f, 0f, [], PlayerPreviewStats.Empty, reason);
}

internal enum PlayerPreviewEmptyReason
{
    None,
    PlayerHidingDisabled,
    TemporaryReveal,
    PlayerUnavailable,
    SuspendedInDuty,
    SuspendedByLowPlayerCount,
    NoOtherPlayers,
}

internal readonly record struct PlayerPreviewEntry(
    uint EntityId,
    int ObjectIndex,
    string Name,
    Vector2 RelativeXZ,
    float Distance,
    bool IsVisible,
    bool IsHiddenByPlugin,
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
                shouldHide,
                keepDecision.RuleId,
                keepDecision.BudgetPolicy,
                keepDecision.Kind == PlayerKeepDecisionKind.Keep ? keepDecision.Rank : null,
                cutByBudget,
                keepDecision.MatchedRules
            )
        );
    }

    public void Add(GameObject* gameObject, int objectIndex, PlayerPreviewEntry previousEntry)
    {
        if (gameObject == null)
        {
            return;
        }

        var (relativeXz, distance) = GetRelativePosition(gameObject);

        if (previousEntry.IsVisible)
        {
            visiblePlayers++;
        }
        else
        {
            hiddenPlayers++;
        }

        players.Add(
            previousEntry with
            {
                EntityId = gameObject->EntityId,
                ObjectIndex = objectIndex,
                RelativeXZ = relativeXz,
                Distance = distance,
            }
        );
    }

    public PlayerPreviewSnapshot Build()
    {
        return new PlayerPreviewSnapshot(
            Environment.TickCount,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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

    // FFXIVClientStructs GameObject._name: FieldOffset(0x30), FixedSizeArray64<byte>.
    private const int GameObjectNameOffset = 0x30;
    private const int GameObjectNameLength = 64;

    public static string GetObjectName(GameObject* gameObject)
    {
        var bytes = new ReadOnlySpan<byte>((byte*)gameObject + GameObjectNameOffset, GameObjectNameLength);
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        if (length == 0)
        {
            return $"#{gameObject->EntityId:X8}";
        }

        var name = Encoding.UTF8.GetString(bytes[..length]);
        return string.IsNullOrWhiteSpace(name) ? $"#{gameObject->EntityId:X8}" : name;
    }
}
