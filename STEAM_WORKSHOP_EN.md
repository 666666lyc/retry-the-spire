[h1]Retry a room, not the whole run[/h1]

Retry the Spire lets you quickly retry the current room from the pause menu or resume a past run from any visited map node. Single-player and multiplayer are both supported.

[b]Important: Do not enable the original Retry mod at the same time.[/b]

[h1]Two ways to retry[/h1]

[h2]Retry the current room[/h2]

[list]
[*][b]Single-player:[/b] Pause the game and select Retry to reload the native save created when you entered the room.
[*][b]Multiplayer, everyone has the mod:[/b] When every connected player uses v0.5.0 or newer, all players reload in place while keeping the current connection.
[*][b]An unmodded player is present:[/b] The host creates a native replacement lobby. Updated peers find and ready in that lobby automatically; unmodded players can rejoin manually without installing another dependency.
[/list]

[h2]Resume from Run History[/h2]

[olist]
[*]Open a run in Run History.
[*]Select [b]View Acts[/b].
[*]Switch to the desired act, select a visited map node, and confirm.
[*]Resume from that node with the recorded run state restored.
[/olist]

Only the host needs Retry the Spire when resuming a multiplayer history entry. Clients receive the save through the game's native load-run lobby.

[h1]What is restored[/h1]

[list]
[*]Seed, character, and ascension
[*]Deck, relics, and potions
[*]HP, gold, and play time
[*]Visited path, current act, and map node
[*]Event history and room queues
[*]Available room-entry player, map, and RNG snapshots
[*]Original multiplayer participants under their recorded Steam IDs
[/list]

Room snapshots are stored beside the active profile saves, allowing Steam Cloud to carry exact retries across devices.

[h1]Installation[/h1]

[olist]
[*]Subscribe to this mod.
[*]Launch Slay the Spire 2 and enable [b]Retry the Spire[/b] from the Mods screen.
[*]Disable or remove the original [b]Retry[/b] mod if it is installed.
[/olist]

[h1]Multiplayer compatibility[/h1]

[list]
[*]Only players who want automatic in-place reload need to install this mod.
[*]Unmodded players can still join normally and manually rejoin the native replacement lobby after a retry.
[*]Gameplay or content mods that register game models must still match on every multiplayer client.
[*]Cosmetic or non-gameplay mods may differ.
[*]v0.4.x uses an incompatible multiplayer protocol. Upgrade to v0.5.0 or newer, or disable the old version before joining a current room.
[/list]

[h1]Known limitations[/h1]

[list]
[*]History entries created before v0.5.1 use best-effort reconstruction when no room snapshot is available. Some combat-only counters and RNG results may differ.
[*]The original Retry and Retry the Spire cannot run together.
[*]The game disables achievements while mods are loaded.
[/list]

[h1]Source and feedback[/h1]

Source code, full documentation, and issue tracker:
[url=https://github.com/666666lyc/retry-the-spire]github.com/666666lyc/retry-the-spire[/url]

This project is derived from Retry by Austin/sts2mods and is distributed under the MIT license.
