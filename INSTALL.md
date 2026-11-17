# Installing Retry the Spire

Current release: **v0.8.0**

Use this same package for main branch **v0.111.1** or public-beta **v0.113.1**.
The Fool, Dread and Blitz are supported on the beta branch. Multiplayer peers
must use the same game build. Complete native history snapshots belong to the
game build that captured them; incompatible snapshots are retained and rejected
before an existing run is abandoned.

1. Disable or remove the original **Retry** mod. Do not enable both mods.
2. Extract the release archive into the *Slay the Spire 2* `mods` directory.
3. Confirm the installed path is
   `mods/RetryTheSpire/RetryTheSpire.dll`.
4. Launch the game and enable **Retry the Spire** from the Mods screen.

When every connected player uses Retry the Spire v0.8.0 or newer, an in-run
multiplayer restart reloads directly without leaving the current lobby. With
an unmodded client, the host uses the native replacement lobby; the unmodded
player needs no extra dependency and can rejoin manually. Versions v0.4.x use
an incompatible network protocol and must be upgraded to v0.8.0 or disabled
before joining. All players still need matching versions of any gameplay or
content mods used by the run. Only players who want automatic in-place reload
need to install Retry the Spire.
