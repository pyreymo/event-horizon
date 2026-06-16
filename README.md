<p align="center">
  <img src="./images/icon.png" alt="Event Horizon icon" width="128">
</p>

<h1 align="center">Event Horizon</h1>

<p align="center">
  Cull what doesn't orbit you.
</p>

<p align="center">
  <a href="https://github.com/pyreymo/event-horizon/releases/latest"><img src="https://img.shields.io/github/v/release/pyreymo/event-horizon?label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Dalamud%20API-15-blue" alt="Dalamud API 15">
</p>

## Overview

**Event Horizon** is a Dalamud plugin for reducing visual clutter in crowded areas.

It hides distant or less relevant player characters while keeping important players visible, such as party members, friends, targets, recent chat participants, and nearby players.

The goal is simple: keep the world readable without turning every crowded city into a wall of character models.

## Install

Add this custom plugin repository in Dalamud:

```text
https://raw.githubusercontent.com/pyreymo/event-horizon/master/repo.json
```

Then install **Event Horizon** from the plugin installer.

## How it works

Event Horizon applies hiding rules to other players around you.

Players are kept visible when they match enabled keep rules. Everyone else may be hidden, faded out, or limited by your configured visibility settings.

Your own character is never hidden.

## Main features

### Smart player visibility

Hide other players in crowded areas while preserving players you are likely to care about.

Keep rules include:

- Friends
- Party and alliance members
- Current target and focus target
- Players targeting you
- Recent chat participants
- Nearby players
- Recruiting players
- Selected race and sex combinations

### Smooth transitions

Players can fade out and back in instead of disappearing abruptly.

Fade transitions can be disabled if you prefer immediate visibility changes.

### Crowd limits

You can limit how many other players remain visible after keep rules are applied.

This is useful in cities, venues, hunt trains, and other crowded scenes.

### Attached object cleanup

For players who remain visible, Event Horizon can also hide their:

- Minions
- Fashion accessories

This reduces extra visual noise without hiding the player themselves.

### Safety options

Event Horizon can automatically suspend hiding:

- In duties
- When the number of other players is below your configured threshold

You can also preview the nearby-player keep range in the world.

## Commands

```text
/eventhorizon
/eh
/eh on
/eh off
/eh toggle
```

`/eventhorizon` and `/eh` are interchangeable.

## Building from source

Install XIVLauncher, Dalamud, and the .NET SDK expected by the Dalamud SDK.

Clone this repository with submodules, then build:

```text
dotnet build .\EventHorizon\EventHorizon.csproj --configuration Release -p:Platform=x64
```

Tagged releases are built by GitHub Actions and published with `EventHorizon.zip`.
