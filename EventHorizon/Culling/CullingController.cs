using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class CullingController : IDisposable
{
    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly DrawAdmissionPolicy policy;
    private readonly NativeDrawCandidateHook candidatesHook;
    private CullingRuntimeMode? currentMode;
    private PlayerObjectIdentity? revealedPlayer;
    private long revealExpiresAt;

    public CullingController(
        IGameInteropProvider interop,
        ISigScanner scanner,
        Configuration configuration,
        IPlayerState playerState,
        ICondition condition,
        IObjectTable objects,
        ITargetManager targets,
        IGameGui gameGui,
        IPluginLog log
    )
    {
        this.configuration = configuration;
        this.playerState = playerState;
        this.condition = condition;
        policy = new(configuration, objects, targets, gameGui);
        candidatesHook = new(interop, scanner, ApplyPolicy, log);
    }

    public bool TemporarilyShowAllPlayers { private get; set; }
    public int HiddenPlayerCount => GetStatus().Rejected;

    public void Enable() => candidatesHook.Enable();

    public void RecordChatMessage(IChatMessage message) => policy.RecordChatMessage(message);

    public void StopReveal() => revealedPlayer = null;

    public void Reveal(PlayerObjectIdentity identity)
    {
        revealedPlayer = identity;
        revealExpiresAt = Environment.TickCount64 + 5_000;
    }

    private uint? GetRevealedEntityId(GameObjectManager* manager)
    {
        if (revealedPlayer is not { } identity || Environment.TickCount64 >= revealExpiresAt || manager == null)
            return null;
        var obj = manager->Objects.GetObjectByEntityId(identity.EntityId);
        return identity.Matches(obj) ? identity.EntityId : null;
    }

    private void ApplyPolicy(Span<NativeDrawCandidate> candidates)
    {
        var manager = GameObjectManager.Instance();
        if (UpdateMode(manager) == CullingRuntimeMode.Active)
            policy.Apply(candidates, manager, GetRevealedEntityId(manager));
    }

    public void Update() => UpdateMode(GameObjectManager.Instance());

    public void Refresh(bool resetRuleState = false)
    {
        if (resetRuleState)
            policy.ClearRules();
        policy.Clear();
        Update();
    }

    public void Dispose()
    {
        candidatesHook.Dispose();
        policy.Clear();
        policy.ClearRules();
        StopReveal();
        // The game rebuilds candidates on its next update; no model restoration is needed.
    }

    private CullingRuntimeMode UpdateMode(GameObjectManager* manager)
    {
        var mode = DetermineMode(manager, CountOtherPlayers(manager));
        if (currentMode != mode)
        {
            policy.Clear();
            StopReveal();
            if (mode is CullingRuntimeMode.Disabled or CullingRuntimeMode.PlayerUnavailable)
                policy.ClearRules();
            currentMode = mode;
        }
        return mode;
    }

    private CullingRuntimeMode DetermineMode(GameObjectManager* manager, int count)
    {
        if (!configuration.HideAllOtherPlayers)
            return CullingRuntimeMode.Disabled;
        if (candidatesHook.Failed)
            return CullingRuntimeMode.NativeHookFailed;
        if (!playerState.IsLoaded || manager == null)
            return CullingRuntimeMode.PlayerUnavailable;
        if (TemporarilyShowAllPlayers)
            return CullingRuntimeMode.SuspendedTemporaryReveal;
        if (configuration.DisableInDuty && (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56]))
            return CullingRuntimeMode.SuspendedDuty;
        if (configuration.DisableCullingBelowPlayerCount && count < configuration.DisableCullingPlayerCountThreshold)
            return CullingRuntimeMode.SuspendedLowPlayerCount;
        return CullingRuntimeMode.Active;
    }

    public CullingStatus GetStatus()
    {
        var manager = GameObjectManager.Instance();
        var count = playerState.IsLoaded ? CountOtherPlayers(manager) : 0;
        var mode = DetermineMode(manager, count);
        var admitted = 0;
        var rejected = 0;
        if (mode == CullingRuntimeMode.Active && currentMode == mode)
        {
            foreach (var decision in policy.Decisions)
            {
                if (decision.Resolve(manager) == null)
                    continue;
                if (decision.Allowed)
                    admitted++;
                else
                    rejected++;
            }
        }
        return new(mode, count, admitted, rejected);
    }

    public List<InspectedPlayer> InspectPlayers()
    {
        var players = new List<InspectedPlayer>();
        var manager = GameObjectManager.Instance();
        if (!playerState.IsLoaded || manager == null)
            return players;
        var local = manager->Objects.IndexSorted[0].Value;
        var position = local == null ? Vector3.Zero : (Vector3)local->Position;
        var status = GetStatus();
        var decisions = new Dictionary<int, PlayerAdmissionDecision>();
        if (status.Mode == CullingRuntimeMode.Active && currentMode == status.Mode)
            foreach (var decision in policy.Decisions)
                if (decision.Resolve(manager) != null)
                    decisions[decision.ObjectIndex] = decision;

        for (var index = CharacterObjectSlots.FirstRemoteSlot; index <= CharacterObjectSlots.LastEvenSlot; index += 2)
        {
            var obj = manager->Objects.IndexSorted[index].Value;
            if (obj == null || obj->ObjectKind != ObjectKind.Pc)
                continue;
            var hasDecision = decisions.TryGetValue(index, out var decision);
            players.Add(
                new(
                    PlayerObjectIdentity.From(obj),
                    obj->NameString,
                    Vector3.Distance(position, obj->Position),
                    hasDecision ? decision : null
                )
            );
        }
        players.Sort(
            (a, b) =>
            {
                var name = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                return name != 0 ? name : a.Identity.GameObjectId.CompareTo(b.Identity.GameObjectId);
            }
        );
        return players;
    }

    private static int CountOtherPlayers(GameObjectManager* manager)
    {
        if (manager == null)
            return 0;
        var count = 0;
        for (var index = CharacterObjectSlots.FirstRemoteSlot; index <= CharacterObjectSlots.LastEvenSlot; index += 2)
        {
            var obj = manager->Objects.IndexSorted[index].Value;
            if (obj != null && obj->ObjectKind == ObjectKind.Pc)
                count++;
        }
        return count;
    }
}

internal enum CullingRuntimeMode
{
    Disabled,
    SuspendedTemporaryReveal,
    PlayerUnavailable,
    SuspendedDuty,
    SuspendedLowPlayerCount,
    NativeHookFailed,
    Active,
}

internal readonly record struct CullingStatus(CullingRuntimeMode Mode, int OtherPlayerCount, int Admitted, int Rejected);

internal readonly record struct InspectedPlayer(
    PlayerObjectIdentity Identity,
    string Name,
    float Distance,
    PlayerAdmissionDecision? Admission
);
