# Event Horizon

Cull what doesn't orbit you.

Event Horizon is a Dalamud plugin scaffold for experimenting with local world-object culling around the player.

## Building

1. Install XIVLauncher, Dalamud, and the .NET SDK expected by the Dalamud SDK.
2. Open `EventHorizon.sln`.
3. Build the solution.

The debug plugin output is written under:

```text
EventHorizon/bin/x64/Debug/EventHorizon.dll
```

## Loading

Add the built DLL path to Dalamud's dev plugin locations, then enable Event Horizon from `/xlplugins`.

Commands:

```text
/eventhorizon
/eh
```

Both commands open the settings window.
