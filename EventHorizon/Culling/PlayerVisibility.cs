using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed class PlayerVisibilityPlan(TimeSpan sampleTime, IReadOnlyList<PlayerVisibilityPlanEntry> entries)
{
    public TimeSpan SampleTime { get; } = sampleTime;
    public IReadOnlyList<PlayerVisibilityPlanEntry> Entries { get; } = entries;
}

internal readonly record struct PlayerVisibilityPlanEntry(
    PlayerObjectIdentity Identity,
    int ObjectIndex,
    PlayerVisibilityClassification Classification,
    PlayerKeepDecision Decision,
    bool CutByBudget,
    Vector3 Position,
    bool HasPosition
)
{
    public bool IsManaged => Classification != PlayerVisibilityClassification.Unmanaged;
}

internal readonly record struct PlayerVisibilityTarget(
    PlayerObjectIdentity Identity,
    int ObjectIndex,
    PlayerVisibilityClassification Classification,
    bool DesiredVisible,
    PlayerKeepDecision Decision,
    bool CutByBudget
);

internal enum PlayerVisibilityClassification
{
    BypassVisible,
    Competitive,
    ForceHidden,
    Unmanaged,
}

internal readonly record struct PlayerObjectIdentity(nint Address, ulong GameObjectId, uint EntityId)
{
    public static unsafe PlayerObjectIdentity From(GameObject* gameObject) =>
        new((nint)gameObject, (ulong)gameObject->GetGameObjectId(), gameObject->EntityId);

    public unsafe bool Matches(GameObject* gameObject) =>
        gameObject != null
        && (nint)gameObject == Address
        && (ulong)gameObject->GetGameObjectId() == GameObjectId
        && gameObject->EntityId == EntityId;
}

internal sealed class PlayerVisibilityAppliedState
{
    private PlayerVisibilityFrameState? activeFrame;

    public PlayerVisibilityFrameState? ActiveFrame => Volatile.Read(ref activeFrame);
    public PlayerVisibilityTarget[]? ActiveTarget => ActiveFrame?.ActiveTarget;

    public void Publish(PlayerVisibilityFrameState frame) => Volatile.Write(ref activeFrame, frame);

    public bool IsExplicitlyVisible(PlayerObjectIdentity identity, int objectIndex)
    {
        var snapshot = ActiveFrame;
        return snapshot != null && snapshot.VisibleSlots.Contains((identity, objectIndex));
    }

    public void Clear() => Volatile.Write(ref activeFrame, null);
}

internal sealed record PlayerVisibilityFrameState
{
    public PlayerVisibilityFrameState(PlayerVisibilityTarget[] activeTarget, PlayerVisibilityAction[] actions)
    {
        ActiveTarget = activeTarget;
        Actions = actions;
        VisibleSlots = activeTarget
            .Where(static target => target.DesiredVisible)
            .Select(static target => (target.Identity, target.ObjectIndex))
            .ToFrozenSet();
    }

    public PlayerVisibilityTarget[] ActiveTarget { get; }
    public PlayerVisibilityAction[] Actions { get; }
    public FrozenSet<(PlayerObjectIdentity Identity, int ObjectIndex)> VisibleSlots { get; }
}

internal readonly record struct PlayerVisibilityAction(
    PlayerVisibilityActionKind Kind,
    PlayerVisibilityTarget Target,
    PlayerVisibilityTarget? PairedTarget
)
{
    public static PlayerVisibilityAction Show(PlayerVisibilityTarget target) => new(PlayerVisibilityActionKind.Show, target, null);

    public static PlayerVisibilityAction Hide(PlayerVisibilityTarget target) => new(PlayerVisibilityActionKind.Hide, target, null);

    public static PlayerVisibilityAction Swap(PlayerVisibilityTarget outgoing, PlayerVisibilityTarget incoming) =>
        new(PlayerVisibilityActionKind.Swap, incoming, outgoing);
}

internal enum PlayerVisibilityActionKind
{
    Show,
    Hide,
    Swap,
}
