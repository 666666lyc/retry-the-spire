# Retry the Spire

Retry the Spire resumes a *Slay the Spire 2* run-history entry from a visited
map node with the recorded seed, deck, relics, potions, HP, gold, and map
progress restored.

> [!IMPORTANT]
> Retry the Spire is a modified fork of
> [sts2mods/Retry](https://github.com/sts2mods/Retry), originally created by
> Austin/sts2mods. Do not enable Retry and Retry the Spire at the same time;
> both patch the same game screens and run-loading paths.

## Features

- Adds **View Acts** to run history and game-over screens.
- Previews every visited act and node using the original map path.
- Restores cards, relics, potions, health, gold, event history, room queues,
  and available RNG snapshots.
- Keeps single-player retries compatible with the upstream custom-run flow.
- Rebuilds multiplayer history as a native `SerializableRun` and opens the
  game's standard load-run lobby.
- Restores every original multiplayer participant under their recorded Steam
  ID. Only those players can join the restored lobby.
- Requires Retry the Spire only on the host. Clients receive the native save
  through the game's normal `LoadRunLobby` flow.
- Keeps multiplayer history lobbies in `GameMode.Standard`, without custom
  modifiers, so the game applies its normal multiplayer ascension rules.
- Keeps the game's independent single-player and multiplayer save slots
  separate.
- Adds **重打** to the in-run pause menu. Single-player reloads the
  native room-entry save immediately; multiplayer hosts reopen that save in
  the native load-run lobby so unmodded friends can rejoin.
- During an in-run multiplayer restart, teammates running v0.4.3 or newer are
  verified over a dedicated reliable control channel before the old lobby is
  closed. They automatically find the replacement lobby and ready up. If a
  compatible teammate does not acknowledge within three seconds, the live run
  stays connected and the host chooses whether to cancel or continue with
  manual rejoining.

## Limitations

- Historical runs created before installing the mod may lack per-floor RNG
  snapshots, so combat-internal RNG can diverge.
- Gameplay/content mods that register models must still match on every
  multiplayer client. Cosmetic or non-gameplay mods may differ.
- The original Retry and Retry the Spire are mutually exclusive at runtime.
- The game disables achievements while mods are loaded.

## Installation

Download `RetryTheSpire-v0.4.3.zip` from the GitHub Releases page and extract
it into the game's `mods` directory. The final layout must be:

```text
mods/
  RetryTheSpire/
    RetryTheSpire.dll
    mod_manifest.json
    LICENSE
    INSTALL.md
```

Disable or remove the original Retry mod before enabling Retry the Spire.

## Building

The project requires the .NET 9 SDK and a local copy of *Slay the Spire 2*.

```bash
./build.sh Release
```

The project references game assemblies from the local installation but does
not redistribute them.

## Attribution and license

Retry the Spire is maintained by **666666lyc** and is derived
from [Retry](https://github.com/sts2mods/Retry), originally created by
Austin/sts2mods.

The original MIT license and copyright notice are preserved unchanged in
[`LICENSE`](LICENSE). Additional attribution for this fork is recorded in
[`NOTICE`](NOTICE). The modifications are distributed under the same MIT
license.
