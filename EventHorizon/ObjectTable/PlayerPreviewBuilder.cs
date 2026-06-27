using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.ObjectTable;

internal sealed unsafe class PlayerPreviewBuilder
{
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
            PlayerPreviewConstants.DefaultViewRange,
            configuration.KeepNearbyPlayers
                ? Math.Clamp(
                    configuration.KeepNearbyPlayersRange,
                    PlayerPreviewConstants.NearbyRangeMin,
                    PlayerPreviewConstants.NearbyRangeMax
                )
                : PlayerPreviewConstants.DisabledNearbyRange,
            Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 100)
        );
    }

    public void Add(GameObject* gameObject, int objectIndex, PlayerKeepDecision keepDecision, bool shouldHide, bool cutByBudget)
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
                GetObjectName(gameObject),
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
            new PlayerPreviewStats(players.Count, visiblePlayers, hiddenPlayers, budgetLimit)
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

    private static string GetObjectName(GameObject* gameObject)
    {
        var bytes = new ReadOnlySpan<byte>(
            (byte*)gameObject + PlayerPreviewConstants.GameObjectNameOffset,
            PlayerPreviewConstants.GameObjectNameLength
        );
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
