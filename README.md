<p align="center">
  <img src="./images/icon.png" alt="Event Horizon icon" width="128">
</p>

<h1 align="center">Event Horizon</h1>
<p align="center">Keep the people who matter in view.</p>

Event Horizon is a Dalamud plugin for controlling crowd density in FFXIV.

## Crowd controls

Set how many ordinary players to admit, then choose which relationships to always keep or prefer. Always-kept players are additional to this limit. Preferences include party and alliance members, friends, targets, nearby players, recent conversations, recruiting players, and selected races and sexes. Other players fill remaining places.

The nearby-player inspector shows each player's decision, reason, and distance. An explicit action temporarily admits a player for five seconds for identification; closing the inspector ends that reveal. The game still controls actual model drawing, including its distance and overall character limits.

Culling can pause in duties, in small crowds, or while holding a reveal shortcut (Ctrl + Alt by default). Other players' minions, fashion accessories, battle pets, and certain nonessential event NPCs can also be hidden. These options follow the same pause conditions. Your own character is excluded from culling.

An optional server-bar entry shows status. Left-click toggles culling; right-click opens the console.

## Install

Add this custom plugin repository in Dalamud, then install **Event Horizon**:

```text
https://raw.githubusercontent.com/pyreymo/event-horizon/master/repo.json
```

## Commands

| Command | Action |
| --- | --- |
| `/eh` | Open or close the crowd console |
| `/eh on` | Enable crowd management |
| `/eh off` | Disable crowd management |
| `/eh toggle` | Toggle crowd management |
| `/eh preview` | Open the nearby-player inspector |

`/eventhorizon` is an alias for `/eh`.

## Building from source

Install XIVLauncher, Dalamud, and the .NET SDK required by the Dalamud SDK.

```text
dotnet build .\EventHorizon\EventHorizon.csproj --configuration Release -p:Platform=x64
```

Tagged releases are built by GitHub Actions and published as `EventHorizon.zip`.
