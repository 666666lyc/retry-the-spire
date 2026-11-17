# Offline engine smoke tests

This is an opt-in test mod, excluded from the product package. It starts and overwrites test runs, supplies a fully unlocked player state through a test-only patch, and exits the game when finished. It refuses to run if Steam is initialized or the user directory does not contain `RetryTheSpireCompat`.

1. Prepare two independent game copies outside the installed game directory, one for v0.111.1 and one for v0.113.1. Use an empty `mods` directory in each copy.
2. Place an `override.cfg` beside each copied executable, using a different custom user directory for each branch:

   ```ini
   config_version=5
   [application]
   config/use_custom_user_dir=true
   config/custom_user_dir_name="RetryTheSpireCompat-20261117-stable"
   ```

3. In that isolated user directory's `default/1/settings.save`, enable the test mods with `{"mod_settings":{"mods_enabled":true,"mod_list":[]},"seen_ea_disclaimer":true,"skip_intro_logo":true}`. Never edit the installed game's profile for this test.
4. Build the production mod against the main branch, then build this project with `dotnet build tests/GameSmoke -c Release -p:STS2GameDir=<main-branch-copy>`. On non-Windows platforms, also pass the actual `STS2GameDataDir`.
5. Install the same production DLL and manifest under `mods/RetryTheSpire/` in both copies. Place `CompatibilitySmoke.dll` from this project's build output and this folder's test manifest under `mods/CompatibilitySmoke/`.
6. Launch each copied executable with `--headless --force-steam=off --path <copy> --log-file <absolute-log-path>`. A `--quit-after 20000` bound can prevent a hung setup from running indefinitely.

Verify the logged `COMPAT_SMOKE_USERDIR` before using the results. Require `Harmony: 45 ok, 0 failed`, all `COMPAT_SMOKE_PASS` scenario lines, and `COMPAT_SMOKE_SUCCESS`. Exceptions produce `COMPAT_SMOKE_FAIL` and a nonzero exit status; a process exit without the success marker is not a pass.

The test covers Standard/Custom on the main branch and the Fool in Standard/Dread/Blitz on beta: combat initialization, exact reload, two shuffle rerolls, encounter reroll, native history restore, and the persisted room-entry RNG words and timer. It also calls the pre-abandon snapshot validation while the original battle is still active. This does not test rendering, the browser, multi-act history reconstruction, or Steam multiplayer.
