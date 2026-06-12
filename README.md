# Event Horizon

Cull what doesn't orbit you.

Event Horizon is a Dalamud plugin that hides other player-related world objects to reduce visual clutter and rendering load in crowded areas.

[![Current release](https://img.shields.io/github/v/release/pyreymo/event-horizon?label=current%20release)](https://github.com/pyreymo/event-horizon/releases/latest)

## Install

Add this custom plugin repository in Dalamud:

```text
https://raw.githubusercontent.com/pyreymo/event-horizon/master/repo.json
```

Then install **Event Horizon** from the plugin installer.

## Commands

```text
/eventhorizon
/eh
/eh on
/eh off
/eh toggle
```

The full `/eventhorizon` command and the short `/eh` command can be used interchangeably.

## Features

### Player hiding

- Hide other players in crowded areas while keeping your own character visible.
- Limit how many other players remain visible after keep rules are applied.

### Keep rules

Keep specific players visible when they match any enabled rule:

- Friends
- Party and alliance members
- Recruiting players
- Recent chat participants
- Current target and focus target
- Players targeting you
- Nearby players
- Selected race/sex combinations

### Attached objects

For players who remain visible, optionally hide:

- Minions
- Fashion accessories

### Safety controls

- Suspend hiding in duties.
- Suspend hiding when the current player count is below your configured threshold.
- Preview the nearby-player keep range in the world.

## Building

1. Install XIVLauncher, Dalamud, and the .NET SDK expected by the Dalamud SDK.
2. Clone this repository with submodules.
3. Build the plugin:

```text
dotnet build .\EventHorizon\EventHorizon.csproj --configuration Release -p:Platform=x64
```

Tagged releases are built by GitHub Actions and published with `EventHorizon.zip`.
