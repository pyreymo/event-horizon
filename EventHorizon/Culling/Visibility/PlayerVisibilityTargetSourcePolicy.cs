using System;
using System.Collections.Generic;

namespace EventHorizon.Culling.Visibility;

internal enum PlayerVisibilityAppliedSource
{
    LegacyFallback,
    StableTopB,
}

internal enum PlayerVisibilityFallbackReason
{
    None,
    Warmup,
    Unavailable,
    SelectionFailed,
    TargetBuildFailed,
}

internal readonly record struct PlayerVisibilityTargetSourceDecision(
    PlayerVisibilityAppliedSource AppliedSource,
    PlayerVisibilityFallbackReason FallbackReason
);

internal static class PlayerVisibilityTargetSourcePolicy
{
    public static PlayerVisibilityTargetSourceDecision DecideTargetSource(PlayerVisibilitySelectionStatus selectionStatus)
    {
        return selectionStatus switch
        {
            PlayerVisibilitySelectionStatus.Ready => new(PlayerVisibilityAppliedSource.StableTopB, PlayerVisibilityFallbackReason.None),
            PlayerVisibilitySelectionStatus.Warmup => new(
                PlayerVisibilityAppliedSource.LegacyFallback,
                PlayerVisibilityFallbackReason.Warmup
            ),
            PlayerVisibilitySelectionStatus.Unavailable => new(
                PlayerVisibilityAppliedSource.LegacyFallback,
                PlayerVisibilityFallbackReason.Unavailable
            ),
            PlayerVisibilitySelectionStatus.Failed => new(
                PlayerVisibilityAppliedSource.LegacyFallback,
                PlayerVisibilityFallbackReason.SelectionFailed
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(selectionStatus),
                selectionStatus,
                "Selection status cannot choose an active target."
            ),
        };
    }
}

internal sealed record PlayerVisibilityActiveTargetResolution(
    PlayerVisibilityTargetSet ActiveTarget,
    PlayerVisibilitySelectionEvaluation Evaluation
);

internal static class PlayerVisibilityActiveTargetResolver
{
    public static PlayerVisibilityActiveTargetResolution Resolve(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet legacyTarget,
        PlayerVisibilitySelectionEvaluation evaluation,
        List<PlayerVisibilityTarget> stableTargetBuffer
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(legacyTarget);
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(stableTargetBuffer);

        PlayerVisibilityTargetSourceDecision decision;
        try
        {
            decision = PlayerVisibilityTargetSourcePolicy.DecideTargetSource(evaluation.Trace.Status);
        }
        catch (Exception exception)
        {
            decision = new(PlayerVisibilityAppliedSource.LegacyFallback, PlayerVisibilityFallbackReason.SelectionFailed);
            evaluation = MarkFailed(evaluation, exception);
        }

        var activeTarget = legacyTarget;
        if (decision.AppliedSource == PlayerVisibilityAppliedSource.StableTopB)
        {
            try
            {
                activeTarget = PlayerVisibilityStableTargetBuilder.Build(plan, evaluation.SelectedIdentities, stableTargetBuffer);
            }
            catch (Exception exception)
            {
                decision = new(PlayerVisibilityAppliedSource.LegacyFallback, PlayerVisibilityFallbackReason.TargetBuildFailed);
                evaluation = MarkFailed(evaluation, exception);
                activeTarget = legacyTarget;
            }
        }

        evaluation = evaluation with
        {
            Trace = evaluation.Trace with
            {
                AppliedSource = decision.AppliedSource,
                FallbackReason = decision.FallbackReason,
                ProposalSelectedCount = evaluation.SelectedIdentities.Count,
                AppliedSelectedCount = PlayerVisibilityActiveBudgetStats.Calculate(activeTarget, 1).VisibleBudgetedPlayerCount,
            },
        };
        return new PlayerVisibilityActiveTargetResolution(activeTarget, evaluation);
    }

    private static PlayerVisibilitySelectionEvaluation MarkFailed(PlayerVisibilitySelectionEvaluation evaluation, Exception exception) =>
        evaluation with
        {
            Trace = evaluation.Trace with
            {
                Status = PlayerVisibilitySelectionStatus.Failed,
                FailureReason = $"{exception.GetType().Name}: {exception.Message}",
            },
        };
}
