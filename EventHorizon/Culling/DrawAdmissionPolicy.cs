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
        Span<int> slots = stackalloc int[candidates.Length];
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
                    candidates[index].Priority = Math.Max(16, candidate.Priority);
                continue;
            }
            if (!CharacterObjectSlots.IsEvenSlot(obj->ObjectIndex))
                continue;

            var decision = rules.GetKeepDecision(obj);
            var position = (System.Numerics.Vector3)obj->Position;
            decision = decision.WithViewport(gameGui.WorldToScreen(position, out _, out var inView) && inView);
            var preview = previewEntityId == obj->EntityId;
            var keep = preview || decision.Kind == PlayerKeepDecisionKind.Keep;
            // Preserve native range rejection. Leave render flags (including the game's
            // special exception) to Update; the plugin budget caps candidates, not draw calls.
            var eligible = candidate.Priority <= 15;
            players.Add(new(candidate, decision, PlayerObjectIdentity.From(obj), index, keep, preview, eligible));
            slots[slotCount++] = index;
        }

        players.Sort(Compare);
        var nextTargets = new PlayerAdmissionDecision[players.Count];
        var counted = 0;
        for (var index = 0; index < players.Count; index++)
        {
            var player = players[index];
            var candidate = player.Candidate;
            var exempt = player.Preview || player.Decision.BudgetPolicy == PlayerKeepBudgetPolicy.Exempt;
            var cut =
                player.Keep
                && player.Eligible
                && !exempt
                && configuration.LimitVisiblePlayerCount
                && counted >= Math.Clamp(configuration.VisiblePlayerCountLimit, 1, 100);
            var rejected = !player.Keep || cut;
            if (!rejected && player.Eligible && !exempt)
                counted++;
            if (rejected)
                candidate.Priority = Math.Max(16, candidate.Priority);

            // Non-player/local-player slots and relative native order remain untouched.
            candidates[slots[index]] = candidate;
            nextTargets[index] = new(player.Identity, candidate.Object->ObjectIndex, !rejected, player.Decision, cut);
        }
        targets = nextTargets;
    }

    private static int Compare(RankedPlayer left, RankedPlayer right)
    {
        var group = Group(left).CompareTo(Group(right));
        if (group != 0)
            return group;
        if (!left.Keep || !left.Eligible)
            return left.NativeIndex.CompareTo(right.NativeIndex);
        var rank = left.Decision.Rank.CompareTo(right.Decision.Rank);
        if (rank != 0)
            return rank;
        var tie = PlayerKeepTieBreaker.Compare(left.Decision.TieBreaker, right.Decision.TieBreaker);
        return tie != 0 ? tie : left.NativeIndex.CompareTo(right.NativeIndex);
    }

    private static int Group(RankedPlayer player) =>
        !player.Keep || !player.Eligible ? 3
        : player.Preview ? 0
        : player.Decision.BudgetPolicy == PlayerKeepBudgetPolicy.Exempt ? 1
        : 2;

    private readonly record struct RankedPlayer(
        NativeDrawCandidate Candidate,
        PlayerKeepDecision Decision,
        PlayerObjectIdentity Identity,
        int NativeIndex,
        bool Keep,
        bool Preview,
        bool Eligible
    );
}
