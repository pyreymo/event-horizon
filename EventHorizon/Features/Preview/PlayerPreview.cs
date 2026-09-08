using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using EventHorizon.Application;
using EventHorizon.Culling;
using EventHorizon.Settings;

namespace EventHorizon.Features.Preview;

internal sealed class PlayerPreview(ICullingReader reader)
{
    public PlayerPreviewSnapshot Snapshot { get; private set; } = new(50, 0, [], PlayerPreviewEmptyReason.PlayerUnavailable);

    public void Refresh()
    {
        var source = reader.Capture();
        var reason = source.Status.Mode switch
        {
            CullingRuntimeMode.Disabled => PlayerPreviewEmptyReason.PlayerHidingDisabled,
            CullingRuntimeMode.SuspendedTemporaryReveal => PlayerPreviewEmptyReason.TemporaryReveal,
            CullingRuntimeMode.SuspendedDuty => PlayerPreviewEmptyReason.SuspendedInDuty,
            CullingRuntimeMode.SuspendedLowPlayerCount => PlayerPreviewEmptyReason.SuspendedByLowPlayerCount,
            CullingRuntimeMode.PlayerUnavailable => PlayerPreviewEmptyReason.PlayerUnavailable,
            CullingRuntimeMode.NativeHookFailed => PlayerPreviewEmptyReason.NativeHookFailed,
            _ => PlayerPreviewEmptyReason.NoOtherPlayers,
        };
        Snapshot = new(50, source.NearbyRange, source.Players.Select(p => new PlayerPreviewEntry(p)).ToArray(), reason);
    }
}

internal sealed record PlayerPreviewSnapshot(
    float ViewRange,
    float NearbyRange,
    IReadOnlyList<PlayerPreviewEntry> Players,
    PlayerPreviewEmptyReason EmptyReason
);

internal enum PlayerPreviewEmptyReason
{
    NoOtherPlayers,
    PlayerHidingDisabled,
    TemporaryReveal,
    PlayerUnavailable,
    SuspendedInDuty,
    SuspendedByLowPlayerCount,
    NativeHookFailed,
}

internal readonly record struct PlayerPreviewEntry(PlayerSnapshot Player)
{
    public PlayerHandle Handle => Player.Handle;
    public uint EntityId => Handle.EntityId;
    public int ObjectIndex => Player.ObjectIndex;
    public string Name => Player.Name;
    public Vector2 RelativeXZ => Player.RelativeXZ;
    public float Distance => Player.Distance;
    public bool? Allowed => Player.Allowed;
    public PlayerKeepRuleId? BestRule => Player.Rule;
    public PlayerKeepBudgetPolicy BudgetPolicy => Player.BudgetPolicy;
    public bool CutByBudget => Player.CutByBudget;
}
