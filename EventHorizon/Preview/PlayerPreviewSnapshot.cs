using System;
using System.Collections.Generic;
using System.Numerics;
using EventHorizon.Culling.Rules;

namespace EventHorizon.Preview;

internal sealed record PlayerPreviewSnapshot(
    int Version,
    long UpdatedAtTicks,
    float ViewRange,
    float NearbyRange,
    IReadOnlyList<PlayerPreviewEntry> Players,
    PlayerPreviewStats Stats
)
{
    public static readonly PlayerPreviewSnapshot Empty = new(
        0,
        0,
        PlayerPreviewConstants.DefaultViewRange,
        PlayerPreviewConstants.DisabledNearbyRange,
        [],
        PlayerPreviewStats.Empty
    );
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
