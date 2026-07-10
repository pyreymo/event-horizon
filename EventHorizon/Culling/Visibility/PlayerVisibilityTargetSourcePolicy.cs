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
    PlayerVisibilitySelectionEvaluation Evaluation,
    Exception? FailureException = null
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

        var decision = PlayerVisibilityTargetSourcePolicy.DecideTargetSource(evaluation.Trace.Status);

        var activeTarget = legacyTarget;
        Exception? failureException = null;
        if (decision.AppliedSource == PlayerVisibilityAppliedSource.StableTopB)
        {
            try
            {
                activeTarget = PlayerVisibilityStableTargetBuilder.Build(plan, evaluation.SelectedKeys, stableTargetBuffer);
            }
            catch (Exception exception)
            {
                failureException = exception;
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
                ProposalSelectedCount = evaluation.SelectedKeys.Count,
            },
        };
        return new PlayerVisibilityActiveTargetResolution(activeTarget, evaluation, failureException);
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
