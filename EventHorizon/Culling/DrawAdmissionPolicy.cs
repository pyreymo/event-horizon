using System;
using System.Collections.Generic;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using EventHorizon.Preview;
using EventHorizon.Settings;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling;

internal sealed unsafe class DrawAdmissionPolicy(
    Configuration configuration,
    IObjectTable objectTable,
    ITargetManager targetManager,
    IGameGui gameGui
)
{
    private readonly PlayerKeepRules rules = new(configuration, objectTable, targetManager);
    private readonly NonPlayerRules nonPlayers = new(configuration);
    private readonly List<RankedPlayer> players = [];
    private PlayerAdmissionDecision[] targets = [];

    public PlayerAdmissionDecision[] Decisions => targets;

    public void RecordChatMessage(IChatMessage message) => rules.RecordChatMessage(message);

    public void ClearRules() => rules.Clear();

    public void Clear() => targets = [];

    public void Apply(Span<NativeDrawCandidate> candidates, GameObjectManager* manager, uint? previewEntityId)
    {
        rules.BeforeUpdate();
        players.Clear();
        Span<int> playerSlots = stackalloc int[candidates.Length];
        var slotCount = 0;
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            var obj = candidate.Object;
            if (
                obj == null
                || obj->ObjectIndex >= manager->Objects.IndexSorted.Length
                || manager->Objects.IndexSorted[obj->ObjectIndex].Value != obj
            )
                throw new InvalidOperationException("Native candidate is not a live manager object.");

            if (obj->ObjectKind != ObjectKind.Pc)
            {
                if (nonPlayers.ShouldHide(obj, manager))
                    candidates[index].Reject();
                continue;
            }
            if (!CharacterObjectSlots.IsEvenSlot(obj->ObjectIndex))
                continue;

            var decision = rules.GetKeepDecision(obj);
            var position = (System.Numerics.Vector3)obj->Position;
            decision = decision.WithViewport(gameGui.WorldToScreen(position, out _, out var inView) && inView);
            var preview = previewEntityId == obj->EntityId;
            // Preserve native range rejection. Leave render flags (including the game's
            // special exception) to Update; the plugin budget caps candidates, not draw calls.
            players.Add(new(candidate, decision, PlayerObjectIdentity.From(obj), index, preview));
            playerSlots[slotCount++] = index;
        }

        players.Sort(Compare);
        var nextTargets = new PlayerAdmissionDecision[players.Count];
        var counted = 0;
        var playerLimit = configuration.LimitVisiblePlayerCount ? Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 100) : int.MaxValue;
        for (var index = 0; index < players.Count; index++)
        {
            var player = players[index];
            var candidate = player.Candidate;
            var consumesBudget = player.Keep && candidate.IsInDrawRange && !player.Exempt;
            var cutByBudget = consumesBudget && counted >= playerLimit;
            var allowed = player.Keep && !cutByBudget;
            if (allowed && consumesBudget)
                counted++;
            if (!allowed)
                candidate.Reject();

            // Sort only within the original remote-player slots; other objects keep their native positions.
            candidates[playerSlots[index]] = candidate;
            nextTargets[index] = new(player.Identity, candidate.Object->ObjectIndex, allowed, player.Decision, cutByBudget);
        }
        targets = nextTargets;
    }

    private static int Compare(RankedPlayer left, RankedPlayer right)
    {
        var group = left.PriorityGroup.CompareTo(right.PriorityGroup);
        if (group != 0)
            return group;
        if (!left.Keep || !left.Candidate.IsInDrawRange)
            return left.NativeIndex.CompareTo(right.NativeIndex);
        var rank = left.Decision.Rank.CompareTo(right.Decision.Rank);
        if (rank != 0)
            return rank;
        var tie = PlayerKeepTieBreaker.Compare(left.Decision.TieBreaker, right.Decision.TieBreaker);
        return tie != 0 ? tie : left.NativeIndex.CompareTo(right.NativeIndex);
    }

    private enum PlayerPriorityGroup
    {
        Preview,
        Exempt,
        Counted,
        RejectedOrOutOfRange,
    }

    private readonly record struct RankedPlayer(
        NativeDrawCandidate Candidate,
        PlayerKeepDecision Decision,
        PlayerObjectIdentity Identity,
        int NativeIndex,
        bool Preview
    )
    {
        public bool Keep => Preview || Decision.HasMatchingRule;
        public bool Exempt => Preview || Decision.BudgetPolicy == PlayerKeepBudgetPolicy.Exempt;

        public PlayerPriorityGroup PriorityGroup =>
            !Keep || !Candidate.IsInDrawRange ? PlayerPriorityGroup.RejectedOrOutOfRange
            : Preview ? PlayerPriorityGroup.Preview
            : Exempt ? PlayerPriorityGroup.Exempt
            : PlayerPriorityGroup.Counted;
    }
}
