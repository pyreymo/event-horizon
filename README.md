<p align="center">
  <img src="./images/icon.png" alt="Event Horizon icon" width="128">
</p>

<h1 align="center">Event Horizon</h1>
<p align="center">Keep the people who matter in view.</p>

Event Horizon is a Dalamud plugin for controlling crowd density in FFXIV.

## Crowd controls

Set how many ordinary players to admit, then choose which relationships to always keep or prefer. Always-kept players are additional to this limit. Preferences include party and alliance members, friends, targets, nearby players, recent conversations, recruiting players, and selected races and sexes. Other players fill remaining places.

Culling can pause in duties, in small crowds, or while holding a reveal shortcut (Ctrl + Alt by default). Other players' minions, fashion accessories, battle pets, and certain nonessential event NPCs can also be hidden. These options follow the same pause conditions. Your own character is excluded from culling.

## Optional features

The **Features** settings tab provides independent switches and settings for:

- Server Info Bar status and FPS, with click-to-toggle crowd controls
- Server Info Bar background styling
- Nameplate, dot, and VFX markers for players targeting you
- Ground VFX or dots for players rejected by crowd management
- Inline and floating player preview, with selection, temporary budget exemption, highlighting, and a direction arrow
- Background objects, terrain, water, grass, and full-scene rendering controls

## Install

Add this custom plugin repository in Dalamud, then install **Event Horizon**:

```text
https://raw.githubusercontent.com/pyreymo/event-horizon/master/repo.json
```

## Commands

| Command | Action |
| --- | --- |
| `/eh` | Open settings |
| `/eh on` | Enable crowd management |
| `/eh off` | Disable crowd management |
| `/eh toggle` | Toggle crowd management |
| `/eh preview` | Toggle floating preview (when the Preview feature is enabled) |

`/eventhorizon` is an alias for `/eh`.

## Building from source

Install XIVLauncher, Dalamud, and the .NET SDK required by the Dalamud SDK.

```text
dotnet build .\EventHorizon\EventHorizon.csproj --configuration Release -p:Platform=x64
```

Tagged releases are built by GitHub Actions and published as `EventHorizon.zip`.
