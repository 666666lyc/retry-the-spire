[h1]Reroll a fight or retry from any visited node[/h1]

Retry the Spire can reroll the encounter or draw order in combat, or resume a past run from any visited map node. Single-player and multiplayer are both supported.

[b]Important: Do not enable the original Retry mod at the same time.[/b]

[h1]Core features[/h1]

[h2]Instant combat rerolls[/h2]

[img]https://images.steamusercontent.com/ugc/9612398864964810110/E3AEC82A58F822362A6680A56ADF21044F6517B9/[/img]

[h3]Reroll Encounter[/h3]

Replace the fight with another complete encounter from the same act and combat tier, then restart from the combat entrance. Weak, regular, elite, and boss encounters remain in their own tiers.

[img]https://images.steamusercontent.com/ugc/16964543852462690622/25986106770C7E297EC95A4FD8871AB5D0EC6277/[/img]

[h3]Reroll Draw Order[/h3]

Advance only the shuffle RNG to change the opening draw and later reshuffles. Monster AI, random targets, and reward RNG stay unchanged.

[img]https://images.steamusercontent.com/ugc/14475840924573603768/90B50F186B3EF49542B96A1C0B4375AB77529842/[/img]

Both controls appear only during combat. In multiplayer, only the host can use them, and every client loads the same transformed save.

[h2]Retry from a visited Run History node[/h2]

[b]1.[/b] Open a run in Run History.
[b]2.[/b] Select [b]View Acts[/b].

[img]https://images.steamusercontent.com/ugc/14548824996800969737/76DA4D5A52920437406C1A030DA47045A8C89DA3/[/img]

[b]3.[/b] Switch to the desired act, select a visited map node, and confirm.
[b]4.[/b] Resume from that node with the recorded run state restored.

[img]https://images.steamusercontent.com/ugc/16099416557635511572/878FA39C82FD7045AE4B42EFED0EEB695E209055/[/img]

Only the host needs Retry the Spire when resuming a multiplayer history entry. Clients receive the save through the game's native load-run lobby.

[h2]Standard room retry[/h2]

Pause the game and select Retry to reload the native save created when you entered the current room.

[img]https://images.steamusercontent.com/ugc/13765178304800972703/DE37A2FA444E366FBFB735B07761E46617D98201/[/img]

[h3]Single-player[/h3]

Restart immediately from the room entrance.

[img]https://images.steamusercontent.com/ugc/16583176595871336671/790008526F6F6CDB5D3484D50CA8B7E1D464797B/[/img]

[h3]Multiplayer: everyone has the mod[/h3]

When every connected player uses v0.5.0 or newer, all players reload in place while keeping the current connection.

[img]https://images.steamusercontent.com/ugc/18351087105714867667/CBF7C67E42B967B77CC76A3497CA376B4C2ACA9F/[/img]

[h3]Multiplayer: an unmodded player is present[/h3]

The host creates a native replacement lobby. Updated peers find and ready in that lobby automatically; unmodded players can rejoin manually without installing another dependency.

[img]https://images.steamusercontent.com/ugc/9821421455064797358/EF60696D25BE48D3CD2A6CCEB1564EC11893ECAD/[/img]

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

[h1]Source and feedback[/h1]

Source code, full documentation, and issue tracker:
[url=https://github.com/666666lyc/retry-the-spire]github.com/666666lyc/retry-the-spire[/url]

This project is derived from Retry by Austin/sts2mods and is distributed under the MIT license.
