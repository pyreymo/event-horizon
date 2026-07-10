# Player visibility architecture

## Execution ownership

`UpdateObjectArrays` detour performs admission only. It observes player slots, maintains admission holds, hard-hides unapproved arrivals, and raises an atomic topology-dirty signal. It does not build visibility plans or execute reconciliations.

Framework owns runtime-mode transitions, refresh planning, target publication, reconciliation execution, fades, non-player visibility, pruning, preview maintenance, and hidden-player VFX.

The native/Dalamud contract does not prove that the detour and `Framework.Update` are serialized. Debug builds therefore record both managed thread IDs, thread changes, overlap, and detour re-entry. The safety boundary does not depend on the probe observing concurrency:

- admission collections are written only by the detour;
- Framework requests admission reset through an atomic generation;
- the next detour consumes that generation;
- diagnostics are atomic snapshots;
- Framework publishes an immutable active frame with `Volatile.Write` and the detour reads it with `Volatile.Read`.

Hook shutdown is ordered as disable, dispose hook, then reset/dispose the culler.

## One execution per Framework frame

A Framework frame follows this order:

1. Determine and synchronize `CullingRuntimeMode`.
2. Consume topology dirty.
3. If active and due/dirty, refresh by constructing and publishing a new frame.
4. If active, execute `FrameworkTick` once.

`ObjectCuller.Update` never executes visibility. Consequently reconciliation, fades, non-player culling, pruning, hidden-player VFX, and the show token bucket advance at most once per Framework frame. Preview-only refresh does not build or execute a reconciliation, and manual full refresh only publishes state for the next Framework tick.

## Runtime modes

The modes are `Disabled`, `PlayerUnavailable`, `SuspendedDuty`, `SuspendedLowPlayerCount`, and `Active`.

Leaving `Active` performs one inactive transition: restore plugin-hidden objects, reset fades and show budget, invalidate preview and budget data, discard the active frame/reconciliation, reset selection history/motion, clear topology dirty, and request admission reset. Repeated frames in the same inactive mode do nothing. `Disabled` additionally clears long-lived keep-rule state; suspension preserves chat interaction rules, configuration, and other long-lived rule state.

Entering `Active` invalidates stale published state and forces a refresh before the frame's only tick, so an old reconciliation cannot execute after suspension.

## Active frame publication

`PlayerVisibilityFrameState` contains one generation's active target, independent reconciliation action array, budget statistics, selection trace, and immutable visible `(identity, objectIndex)` index. The pipeline computes the entire frame before publishing it. Selection history is committed from the same successfully published target.

The active target is desired state. `HiddenObjectTracker` remains the authority for plugin-applied hidden flags.

## Identity and slots

`PlayerObjectIdentity` protects against address reuse by combining address, game-object ID, and entity ID. It does not identify a slot. Current selection proposals therefore retain `SourceIndex`, identity, and `ObjectIndex`; stable target construction validates all three and maps by source index.

Admission keeps every current slot for an identity instead of silently overwriting duplicates. A duplicate/transitioning identity is approved only when every observed slot is explicitly visible in the published snapshot. Hidden records update their recorded slot when the same live object moves, while address reuse by another identity only drops the stale record and never changes the new object's flags.

## Fallback boundary

Warmup, unavailable game data, selection failure, and stable-target mapping failure may select the legacy target. Invalid policy enum/state is a programming error and is not converted into a normal fallback. Budget statistics and applied-selection trace enrichment happen once when the complete active frame is built.
