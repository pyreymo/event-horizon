using System;
using System.Collections.Generic;

namespace EventHorizon.ObjectTable;

internal sealed class PlayerKeepPlan
{
    private readonly Dictionary<nint, PlayerKeepDecision> keepDecisions;
    private readonly HashSet<nint>? visibleCompetitivePlayers;

    private PlayerKeepPlan(Dictionary<nint, PlayerKeepDecision> keepDecisions, HashSet<nint>? visibleCompetitivePlayers)
    {
        this.keepDecisions = keepDecisions;
        this.visibleCompetitivePlayers = visibleCompetitivePlayers;
    }

    public int ProtectedPlayerCount { get; private init; }
    public int VisibleCompetitivePlayerCount { get; private init; }

    public static PlayerKeepPlan Build(Configuration configuration, IReadOnlyList<PlayerKeepCandidate> candidates)
    {
        var keepDecisions = new Dictionary<nint, PlayerKeepDecision>();
        foreach (var candidate in candidates)
        {
            keepDecisions[candidate.Address] = candidate.KeepDecision;
        }

        var visibleCompetitivePlayers = GetVisibleCompetitivePlayers(configuration, candidates);
        return new PlayerKeepPlan(keepDecisions, visibleCompetitivePlayers)
        {
            ProtectedPlayerCount = CountProtectedPlayers(candidates),
            VisibleCompetitivePlayerCount = CountVisibleCompetitivePlayers(candidates, visibleCompetitivePlayers),
        };
    }

    public bool ShouldHide(nint address)
    {
        if (!keepDecisions.TryGetValue(address, out var keepDecision))
        {
            return true;
        }

        return keepDecision.Kind switch
        {
            PlayerKeepDecisionKind.Protected => false,
            PlayerKeepDecisionKind.Competitive => visibleCompetitivePlayers?.Contains(address) == false,
            _ => true,
        };
    }

    private static HashSet<nint>? GetVisibleCompetitivePlayers(Configuration configuration, IReadOnlyList<PlayerKeepCandidate> candidates)
    {
        if (!configuration.LimitVisiblePlayerCount)
        {
            return null;
        }

        var competitivePlayers = new List<PlayerKeepCandidate>();
        foreach (var candidate in candidates)
        {
            if (candidate.KeepDecision.Kind == PlayerKeepDecisionKind.Competitive)
            {
                competitivePlayers.Add(candidate);
            }
        }

        competitivePlayers.Sort(CompareCompetitivePlayers);

        var visiblePlayerLimit = Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 100);
        var visiblePlayers = new HashSet<nint>();
        for (var i = 0; i < competitivePlayers.Count && i < visiblePlayerLimit; i++)
        {
            visiblePlayers.Add(competitivePlayers[i].Address);
        }

        return visiblePlayers;
    }

    private static int CountProtectedPlayers(IReadOnlyList<PlayerKeepCandidate> candidates)
    {
        var count = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.KeepDecision.Kind == PlayerKeepDecisionKind.Protected)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountVisibleCompetitivePlayers(
        IReadOnlyList<PlayerKeepCandidate> candidates,
        HashSet<nint>? visibleCompetitivePlayers
    )
    {
        if (visibleCompetitivePlayers != null)
        {
            return visibleCompetitivePlayers.Count;
        }

        var count = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.KeepDecision.Kind == PlayerKeepDecisionKind.Competitive)
            {
                count++;
            }
        }

        return count;
    }

    private static int CompareCompetitivePlayers(PlayerKeepCandidate left, PlayerKeepCandidate right)
    {
        var rankComparison = left.KeepDecision.Rank.CompareTo(right.KeepDecision.Rank);
        if (rankComparison != 0)
        {
            return rankComparison;
        }

        var distanceComparison = left.KeepDecision.DistanceSq.CompareTo(right.KeepDecision.DistanceSq);
        if (distanceComparison != 0)
        {
            return distanceComparison;
        }

        var entityComparison = left.EntityId.CompareTo(right.EntityId);
        return entityComparison != 0 ? entityComparison : left.Address.ToInt64().CompareTo(right.Address.ToInt64());
    }
}

internal readonly record struct PlayerKeepBudgetStats(
    int ProtectedPlayerCount,
    int VisibleCompetitivePlayerCount,
    int VisibleCompetitivePlayerLimit
);

internal readonly record struct PlayerKeepCandidate(nint Address, PlayerKeepDecision KeepDecision, uint EntityId);

internal enum PlayerKeepDecisionKind
{
    None,
    Protected,
    Competitive,
}

internal readonly record struct PlayerKeepDecision(PlayerKeepDecisionKind Kind, int Rank, float DistanceSq)
{
    public static readonly PlayerKeepDecision None = new(PlayerKeepDecisionKind.None, 0, 0f);

    public static readonly PlayerKeepDecision Protected = new(PlayerKeepDecisionKind.Protected, 0, 0f);

    public static PlayerKeepDecision Competitive(int rank, float distanceSq) => new(PlayerKeepDecisionKind.Competitive, rank, distanceSq);
}
