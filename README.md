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

Players are kept visible when they match enabled keep rules. Those rules can be reordered, and each rule can either count toward or bypass your visible-player budget.

Everyone else may be hidden, faded out, or limited by your configured visibility settings.

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

Keep rules can be reordered, enabled individually, and configured to either use or ignore the visible-player budget.

### Smooth transitions

Players can fade out and back in instead of disappearing abruptly.

Fade transitions can be disabled if you prefer immediate visibility changes.

### Crowd limits

You can limit how many other players remain visible after keep rules are applied.

This is useful in cities, venues, hunt trains, and other crowded scenes. Budget-exempt rules can keep important players visible beyond that limit.

### Targeting-me marker

Event Horizon can mark players who are targeting you.

The marker can use a native nameplate icon, an optional world VFX marker, or both. You can tune its placement, scale, opacity, glow strength, and color from the behavior settings.

This is useful when you want to keep the "players targeting me" rule visible and easy to verify in crowded scenes.

### Live preview

The settings window includes a live preview of nearby players, showing who is visible or hidden, which keep rule matched, whether that player counts toward the budget, and the nearby-player keep range.

Preview dots can be selected while tuning rules, which makes it easier to see why a specific player is being kept or hidden.

The preview can also be popped out into a square floating window from the settings panel or with `/eh preview`.

Hovering or selecting a preview dot draws an in-world direction arrow from your character to that player. Selected preview players are temporarily kept visible and receive an orange world highlight while you inspect their rule and budget state.

### Attached object cleanup

For players who remain visible, Event Horizon can also hide their:

- Minions
- Fashion accessories

This reduces extra visual noise without hiding the player themselves.

### Server info bar controls

Event Horizon can add a compact Server Info Bar entry for quick status and toggling.

The entry can show the current player-hiding state, optionally include FPS, and can be removed entirely from the behavior settings. An experimental native background can also be enabled for the full Server Info Bar.

### Safety options

Event Horizon can automatically suspend hiding:

- In duties
- When the number of other players is below your configured threshold

You can also preview the nearby-player keep range in the world.

Experimental visual aids can show hidden-player ground markers, but these are optional and disabled unless you enable them.

## Commands

```text
/eventhorizon
/eh
/eh on
/eh off
/eh toggle
/eh preview
```

`/eventhorizon` and `/eh` are interchangeable.

`/eh preview` toggles the floating player preview window.

## Project layout

The plugin code is grouped by responsibility:

- `EventHorizon/Culling` contains the object-table hook, visibility updates, fade handling, and keep-rule decisions.
- `EventHorizon/Integration` contains native UI, nameplate marker, DTR, and VFX integrations.
- `EventHorizon/Preview` contains preview snapshots, preview rendering, the floating preview window, world arrows, and selected-player highlights.
- `EventHorizon/UI/Config` contains settings tabs and shared config-window layout helpers.
- `EventHorizon/Settings` contains persisted plugin configuration.

## Building from source

Install XIVLauncher, Dalamud, and the .NET SDK expected by the Dalamud SDK.

Clone this repository with submodules, then build:

```text
dotnet build .\EventHorizon\EventHorizon.csproj --configuration Release -p:Platform=x64
```

Tagged releases are built by GitHub Actions and published with `EventHorizon.zip`.
