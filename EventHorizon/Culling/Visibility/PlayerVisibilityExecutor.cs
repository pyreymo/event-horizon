using System;
using System.Collections.Generic;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerVisibilityExecutor
{
    public static void Execute(
        PlayerVisibilityReconciliation reconciliation,
        Action<PlayerVisibilityAction> executeTransition,
        Action<IReadOnlyList<PlayerVisibilityAction>> executeMaintained
    )
    {
        foreach (var action in reconciliation.Actions)
        {
            if (action.Reason != PlayerVisibilityActionReason.Maintain)
            {
                executeTransition(action);
            }
        }

        executeMaintained(reconciliation.Actions);
    }
}
