using System;
using System.Collections.Generic;
using System.Numerics;
using EventHorizon.Culling.Rules;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerVisibilityPipeline
{
    private readonly PlayerVisibilitySelectionController selectionController;
    private readonly PlayerVisibilityReconciler reconciler = new();
    private readonly Action<Exception> reportFailure;
    private readonly List<PlayerVisibilityPlanEntry> planEntryBuffer = [];
    private readonly List<PlayerVisibilityTarget> legacyTargetBuffer = [];
    private readonly List<PlayerVisibilityTarget> stableTargetBuffer = [];

    public PlayerVisibilityPipeline(Action<Exception> reportSelectionFailure)
    {
        reportFailure = reportSelectionFailure;
        selectionController = new(reportFailure: reportSelectionFailure);
    }

    public PlayerVisibilityFrameState BuildFrame(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet legacyTarget,
        bool limitVisiblePlayerCount,
        int visiblePlayerCountLimit,
        Vector3? localPosition,
        HiddenObjectTracker hiddenObjectTracker
    )
    {
        var evaluation = selectionController.Evaluate(plan, legacyTarget, limitVisiblePlayerCount, visiblePlayerCountLimit, localPosition);
        var resolution = PlayerVisibilityActiveTargetResolver.Resolve(plan, legacyTarget, evaluation, stableTargetBuffer);
        if (resolution.FailureException != null)
        {
            reportFailure(resolution.FailureException);
        }
        var budgetStats = PlayerVisibilityActiveBudgetStats.Calculate(resolution.ActiveTarget, visiblePlayerCountLimit);
        var trace = resolution.Evaluation.Trace with { AppliedSelectedCount = budgetStats.VisibleBudgetedPlayerCount };
        var reconciliation = reconciler.Reconcile(resolution.ActiveTarget, hiddenObjectTracker);
        return new PlayerVisibilityFrameState(resolution.ActiveTarget, reconciliation, budgetStats, trace);
    }

    public void Commit(PlayerVisibilityFrameState frame) => selectionController.CommitAppliedTarget(frame.ActiveTarget);

    public void Reset() => selectionController.Reset();

    public unsafe PlayerVisibilityPlan BuildPlan(
        int generation,
        GameObjectManager* manager,
        PlayerKeepPlan keepPlan,
        uint? previewVisibleEntityId
    ) => PlayerVisibilityPlan.Build(generation, manager, keepPlan, previewVisibleEntityId, planEntryBuffer);

    public PlayerVisibilityTargetSet BuildLegacyTarget(PlayerVisibilityPlan plan) =>
        PlayerVisibilityLegacyTargetBuilder.Build(plan, legacyTargetBuffer);
}
