using System;
using System.Collections.Generic;

namespace EventHorizon.Culling.Visibility;

internal enum PlayerVisibilityTargetSource
{
    Legacy,
    StableTopB,
}

internal enum PlayerVisibilityFallbackReason
{
    None,
    ConfiguredLegacy,
    Warmup,
    Unavailable,
    SelectionFailed,
    TargetBuildFailed,
}

internal readonly record struct PlayerVisibilityTargetSourceDecision(
    PlayerVisibilityTargetSource ConfiguredSource,
    PlayerVisibilityTargetSource AppliedSource,
    PlayerVisibilityFallbackReason FallbackReason
);

internal static class PlayerVisibilityTargetSourcePolicy
{
    public static PlayerVisibilityTargetSourceDecision DecideTargetSource(
        PlayerVisibilityTargetSource configuredSource,
        PlayerVisibilitySelectionStatus selectionStatus
    )
    {
        if (configuredSource == PlayerVisibilityTargetSource.Legacy)
        {
            return new(configuredSource, PlayerVisibilityTargetSource.Legacy, PlayerVisibilityFallbackReason.ConfiguredLegacy);
        }

        return selectionStatus switch
        {
            PlayerVisibilitySelectionStatus.Ready => new(
                configuredSource,
                PlayerVisibilityTargetSource.StableTopB,
                PlayerVisibilityFallbackReason.None
            ),
            PlayerVisibilitySelectionStatus.Warmup => new(
                configuredSource,
                PlayerVisibilityTargetSource.Legacy,
                PlayerVisibilityFallbackReason.Warmup
            ),
            PlayerVisibilitySelectionStatus.Unavailable => new(
                configuredSource,
                PlayerVisibilityTargetSource.Legacy,
                PlayerVisibilityFallbackReason.Unavailable
            ),
            PlayerVisibilitySelectionStatus.Failed => new(
                configuredSource,
                PlayerVisibilityTargetSource.Legacy,
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
    PlayerVisibilityTargetSourceDecision SourceDecision,
    PlayerVisibilitySelectionEvaluation Evaluation
);

internal static class PlayerVisibilityActiveTargetResolver
{
    public static PlayerVisibilityActiveTargetResolution Resolve(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet legacyTarget,
        PlayerVisibilitySelectionEvaluation evaluation,
        PlayerVisibilityTargetSource configuredSource,
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
            decision = PlayerVisibilityTargetSourcePolicy.DecideTargetSource(configuredSource, evaluation.Trace.Status);
        }
        catch (Exception exception)
        {
            decision = new(configuredSource, PlayerVisibilityTargetSource.Legacy, PlayerVisibilityFallbackReason.SelectionFailed);
            evaluation = MarkFailed(evaluation, exception);
        }

        var activeTarget = legacyTarget;
        if (decision.AppliedSource == PlayerVisibilityTargetSource.StableTopB)
        {
            try
            {
                activeTarget = PlayerVisibilityStableTargetBuilder.Build(plan, evaluation.SelectedIdentities, stableTargetBuffer);
            }
            catch (Exception exception)
            {
                decision = new(configuredSource, PlayerVisibilityTargetSource.Legacy, PlayerVisibilityFallbackReason.TargetBuildFailed);
                evaluation = MarkFailed(evaluation, exception);
                activeTarget = legacyTarget;
            }
        }

        evaluation = evaluation with
        {
            Trace = evaluation.Trace with
            {
                ConfiguredSource = decision.ConfiguredSource,
                AppliedSource = decision.AppliedSource,
                FallbackReason = decision.FallbackReason,
                ProposalSelectedCount = evaluation.SelectedIdentities.Count,
                AppliedSelectedCount = PlayerVisibilityActiveBudgetStats.Calculate(activeTarget, 1).VisibleBudgetedPlayerCount,
            },
        };
        return new PlayerVisibilityActiveTargetResolution(activeTarget, decision, evaluation);
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
