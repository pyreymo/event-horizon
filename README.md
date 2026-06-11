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
```

Both commands open the settings window.

## Features

- Hide other players and their related objects.
- Keep friends, party/alliance members, recruiting players, recent chat players, targets, players targeting you, nearby players, and selected races.
- Pause culling when the current object table has fewer than a configured number of player characters.
- Apply a fallback cap for the number of visible kept players.
- Preview nearby-player keep range in the world.

## Building

1. Install XIVLauncher, Dalamud, and the .NET SDK expected by the Dalamud SDK.
2. Clone this repository with submodules.
3. Build the plugin:

```text
dotnet build .\EventHorizon\EventHorizon.csproj --configuration Release -p:Platform=x64
```

Tagged releases are built by GitHub Actions and published with `EventHorizon.zip`.
