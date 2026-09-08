using System;
using System.Collections.Generic;
using System.Numerics;
using EventHorizon.Culling;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Application;

internal readonly record struct PlayerHandle(ulong GameObjectId, uint EntityId);

internal sealed record PlayerSnapshot(
    PlayerHandle Handle,
    int ObjectIndex,
    string Name,
    Vector3 Position,
    Vector2 RelativeXZ,
    float Distance,
    float Rotation,
    bool? Allowed,
    bool InDrawRange,
    PlayerKeepRuleId? Rule,
    PlayerKeepBudgetPolicy BudgetPolicy,
    int Rank,
    bool InViewport,
    bool CutByBudget,
    bool TemporaryReveal
);

internal sealed record CullingSnapshot(CullingStatus Status, float NearbyRange, int Budget, IReadOnlyList<PlayerSnapshot> Players);

internal interface ICullingReader
{
    CullingStatus GetStatus();
    CullingSnapshot Capture();
}

internal interface ICullingCommands
{
    bool Enabled { get; }
    void SetEnabled(bool enabled);
    IPlayerReveal AcquireReveal(PlayerHandle player);
}

// Application adapter: features receive value snapshots, never native candidates or mutable core settings.
internal sealed unsafe class CullingApi(CullingController controller, Configuration configuration, Action<bool> setEnabled)
    : ICullingReader,
        ICullingCommands
{
    private CullingSnapshot? snapshot;
    private long nextCapture;

    public bool Enabled => configuration.HideAllOtherPlayers;

    public void SetEnabled(bool enabled) => setEnabled(enabled);

    public CullingStatus GetStatus() => controller.GetStatus();

    public CullingSnapshot Capture()
    {
        var status = controller.GetStatus();
        var now = Environment.TickCount64;
        if (snapshot != null && now < nextCapture && snapshot.Status == status)
            return snapshot;
        var result = new List<PlayerSnapshot>();
        var manager = GameObjectManager.Instance();
        if (manager != null)
        {
            var local = manager->Objects.IndexSorted[0].Value;
            var origin = local == null ? Vector3.Zero : (Vector3)local->Position;
            foreach (var player in controller.InspectPlayers())
            {
                var obj = manager->Objects.GetObjectByEntityId(player.Identity.EntityId);
                if (!player.Identity.Matches(obj))
                    continue;
                var position = (Vector3)obj->Position;
                var decision = player.Admission;
                result.Add(
                    new(
                        new(player.Identity.GameObjectId, player.Identity.EntityId),
                        obj->ObjectIndex,
                        player.Name,
                        position,
                        new(position.X - origin.X, position.Z - origin.Z),
                        player.Distance,
                        obj->Rotation,
                        decision?.Allowed,
                        decision?.InDrawRange ?? false,
                        decision?.Decision.RuleId,
                        decision?.Decision.BudgetPolicy ?? PlayerKeepBudgetPolicy.Counted,
                        decision?.Decision.Rank ?? int.MaxValue,
                        decision?.Decision.TieBreaker.InViewport ?? false,
                        decision?.CutByBudget ?? false,
                        decision?.TemporaryReveal ?? false
                    )
                );
            }
        }
        nextCapture = now + 33;
        snapshot = new(
            status,
            configuration.KeepNearbyPlayers ? configuration.KeepNearbyPlayersRange : 0,
            configuration.LimitVisiblePlayerCount ? configuration.VisiblePlayerCountLimit : int.MaxValue,
            result.AsReadOnly()
        );
        return snapshot;
    }

    public IPlayerReveal AcquireReveal(PlayerHandle player)
    {
        var manager = GameObjectManager.Instance();
        var obj = manager == null ? null : manager->Objects.GetObjectByEntityId(player.EntityId);
        if (obj == null || (ulong)obj->GetGameObjectId() != player.GameObjectId)
            return new ExpiredReveal();
        return controller.AcquireReveal(PlayerObjectIdentity.From(obj));
    }

    private sealed class ExpiredReveal : IPlayerReveal
    {
        public void Renew() { }

        public void Dispose() { }
    }
}
