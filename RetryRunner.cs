// Orchestrator for the retry flow. History-list click and act-map
// view click both funnel here. We assemble a RetryTarget from
// inputs, reconstruct player state, build a SerializableRun, and
// drive it through the same load sequence NMainMenu's "Continue
// Run" uses:
//
//   RunState.FromSerializable(save)
//     → RunManager.Instance.SetUpSavedSinglePlayer(runState, save)
//       → NetSingleplayerGameService init
//         → NGame.Instance.LoadRun(runState, preFinishedRoom)
//
// The optional targetCoord pins the player to a specific (row, col).
// Without it the player lands at "act start" on an unvisited map —
// useful for a soft retry where they walk from the beginning.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

public static class RetryRunner
{
    // Entry from HistoryClickPatch — user clicked a node in the per-
    // run history list (no specific map coord). We open the act-map
    // viewer for that act with the target highlighted so the player
    // confirms a specific node before the actual run kicks off. Once
    // ActMapViewer is wired up, this will hand off to it; until then
    // it just logs.
    public static void OnHistoryNodeClicked(
        RunHistory history,
        MapPointHistoryEntry entry,
        int floor,
        RunHistoryPlayer? player)
    {
        if (player == null)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}history click: no player");
            return;
        }
        var (actIndex, floorIndex) = LocateInHistory(history, entry);
        if (actIndex < 0)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}history click: entry not found in history");
            return;
        }
        GD.Print($"{RetryMod.LogPrefix}history click resolved to act={actIndex} floor={floorIndex} seed={history.Seed}");
        if (history.Players.Count > 1)
        {
            // A multiplayer history must retain every recorded player and use
            // the native multiplayer save slot. The single-player new-run
            // pipeline below intentionally reconstructs only one selected
            // player and would otherwise overwrite current_run.save.
            NActMapBrowser.Open(history, player);
            return;
        }
        // For now launch the retry directly (no coord — player lands
        // at act start). Once ActMapViewer lands the path becomes
        // "open viewer → user picks coord → Begin()".
        Begin(history, player, actIndex, floorIndex, targetCoord: null);
    }

    // Public entry used by ActMapViewer (and the direct history click
    // path above). Snapshots state at the (actIndex, floorIndex)
    // boundary and launches.
    public static void Begin(
        RunHistory history,
        RunHistoryPlayer player,
        int actIndex,
        int floorIndex,
        MapCoord? targetCoord)
    {
        // Either condition triggers our 3-option modal: in-progress
        // (mid-run retry) or a saved run on disk (would clobber).
        // Cancel keeps state; AbandonSave writes a run_history entry
        // and deletes the save; AbandonNoSave skips the history write
        // (avoids flooding history with quick test retries) but still
        // tears the run down.
        try
        {
            bool inProgress = RunManager.Instance?.IsInProgress == true;
            bool hasSave = SaveManager.Instance?.HasRunSave == true;
            if (inProgress || hasSave)
            {
                string title = inProgress ? "Abandon current run?" : "Abandon saved run?";
                string body = inProgress
                    ? "Starting a retry will end your in-progress run."
                    : "Starting a retry will overwrite your existing saved run.";
                RetryAbandonModal.Show(title, body, choice =>
                {
                    switch (choice)
                    {
                        case RetryAbandonModal.Choice.Cancel:
                            GD.Print($"{RetryMod.LogPrefix}retry cancelled by user");
                            return;
                        case RetryAbandonModal.Choice.AbandonSave:
                            PerformAbandon(writeHistory: true, inProgress: inProgress);
                            BeginInternal(history, player, actIndex, floorIndex, targetCoord);
                            break;
                        case RetryAbandonModal.Choice.AbandonNoSave:
                            PerformAbandon(writeHistory: false, inProgress: inProgress);
                            BeginInternal(history, player, actIndex, floorIndex, targetCoord);
                            break;
                    }
                });
                return;
            }
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}Begin guard: {ex.Message}"); }
        BeginInternal(history, player, actIndex, floorIndex, targetCoord);
    }

    public static bool NeedsAbandonPrompt()
    {
        try
        {
            return RunManager.Instance?.IsInProgress == true
                || SaveManager.Instance?.HasRunSave == true;
        }
        catch { return false; }
    }

    // Tear down the current/saved run. When writeHistory is true we
    // mirror NMainMenu.AbandonRun: UpdateProgressWithRunData + a
    // RunHistoryUtilities.CreateRunHistoryEntry stamped as abandoned.
    // When false (Choice.AbandonNoSave) we skip the history write to
    // keep test-retry sessions from spamming the run history list.
    public static void PerformAbandon(bool writeHistory, bool inProgress)
    {
        try
        {
            SerializableRun? save = null;
            if (inProgress)
            {
                try { save = RunManager.Instance?.ToSave(preFinishedRoom: null); }
                catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}abandon ToSave: {ex.Message}"); }
            }
            else
            {
                var read = SaveManager.Instance?.LoadRunSave();
                if (read != null && read.Success) save = read.SaveData;
            }

            if (writeHistory && save != null)
            {
                try
                {
                    SaveManager.Instance.UpdateProgressWithRunData(save, victory: false);
                    MegaCrit.Sts2.Core.Runs.RunHistoryUtilities.CreateRunHistoryEntry(
                        save, victory: false, isAbandoned: true, save.PlatformType);
                }
                catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}abandon write history: {ex.Message}"); }
            }

            if (inProgress)
            {
                // graceful: true so CombatManager.Reset actually nulls
                // its _state. With graceful: false the cleanup block
                // (Reset graceful=true path, line 638 of CombatManager)
                // is skipped, leaving stale combat state — the next
                // run's combat entry then throws "Make sure to reset
                // the combat before setting up a new one." in
                // SetUpCombat. Non-combat retry targets (event /
                // campfire / treasure) don't hit SetUpCombat so the
                // bug only manifested on combat retries.
                try { RunManager.Instance.CleanUp(graceful: true); }
                catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}abandon CleanUp: {ex.Message}"); }
            }
            try { SaveManager.Instance?.DeleteCurrentRun(); }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}abandon DeleteCurrentRun: {ex.Message}"); }

            GD.Print($"{RetryMod.LogPrefix}abandon: writeHistory={writeHistory} inProgress={inProgress}");
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}PerformAbandon: {ex.Message}"); }
    }

    // Multiplayer uses a separate native save slot
    // (current_run_mp.save). Never route this through PerformAbandon:
    // that method deliberately targets current_run.save and would erase
    // an unrelated single-player run. This mirrors the game's own
    // NMultiplayerSubmenu.TryAbandonMultiplayerRun flow.
    public static void PerformMultiplayerAbandon(bool writeHistory, ulong localPlayerId)
    {
        try
        {
            SerializableRun? save = null;
            var read = SaveManager.Instance?.LoadAndCanonicalizeMultiplayerRunSave(localPlayerId);
            if (read != null && read.Success)
                save = read.SaveData;

            if (writeHistory && save != null)
            {
                try
                {
                    SaveManager.Instance.UpdateProgressWithRunData(save, victory: false);
                    MegaCrit.Sts2.Core.Runs.RunHistoryUtilities.CreateRunHistoryEntry(
                        save, victory: false, isAbandoned: true, save.PlatformType);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}multiplayer abandon write history: {ex.Message}");
                }
            }

            try { SaveManager.Instance?.DeleteCurrentMultiplayerRun(); }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}abandon DeleteCurrentMultiplayerRun: {ex.Message}");
            }

            GD.Print(
                $"{RetryMod.LogPrefix}multiplayer abandon: " +
                $"writeHistory={writeHistory} localPlayerId={localPlayerId}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}PerformMultiplayerAbandon: {ex.Message}");
        }
    }

    private static void BeginInternal(
        RunHistory history,
        RunHistoryPlayer player,
        int actIndex,
        int floorIndex,
        MapCoord? targetCoord)
    {
        try
        {
            // Defensive: bail clearly if the run record is broken
            // rather than crashing inside LoadRun.
            if (history == null) { GD.PrintErr($"{RetryMod.LogPrefix}Begin: history null"); return; }
            if (string.IsNullOrEmpty(history.Seed))
            {
                GD.PrintErr($"{RetryMod.LogPrefix}Begin: history has no seed");
                return;
            }
            if (history.Acts == null || history.Acts.Count == 0)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}Begin: history has no acts");
                return;
            }
            if (actIndex < 0 || actIndex >= history.Acts.Count)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}Begin: actIndex {actIndex} out of range");
                return;
            }
            if (ModelDb.GetByIdOrNull<CharacterModel>(player.Character) == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}Begin: character {player.Character} not found in current ModelDb — was this run on a different version / with different mods?");
                return;
            }

            var snapshot = StateReconstructor.ReconstructAtTarget(
                history, actIndex, floorIndex, player);
            var target = new RetryTarget
            {
                Seed = history.Seed,
                SourceStartTime = history.StartTime,
                Ascension = history.Ascension,
                GameMode = history.GameMode,
                ActIds = new List<ModelId>(history.Acts),
                Modifiers = new List<SerializableModifier>(history.Modifiers),
                TargetActIndex = actIndex,
                TargetFloorIndex = floorIndex,
                MapPointHistorySoFar = TruncateHistory(history, actIndex, floorIndex),
                Player = snapshot,
                OriginalRunTime = history.RunTime,
                OriginalTotalFloors = SumFloors(history),
            };
            GD.Print($"{RetryMod.LogPrefix}Begin: seed={target.Seed} act={actIndex} floor={floorIndex} char={player.Character} deck={snapshot.Deck.Count} relics={snapshot.Relics.Count} hp={snapshot.CurrentHp}/{snapshot.MaxHp} gold={snapshot.Gold}");
            _ = LaunchAsync(target);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}Begin: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static async Task LaunchAsync(RetryTarget target)
    {
        // RetryContext is read by Harmony patches that need to alter
        // standard new-run behavior:
        //   • IsRetrying gates everything (so unrelated new runs the
        //     player starts from the menu are untouched).
        //   • SkipNextNeowEntry suppresses the single auto-Neow
        //     EnterMapCoord inside EnterAct(0). We can't clear the
        //     StartedWithNeow flag instead, because GenerateMap reads
        //     it and rewrites StartingMapPoint.PointType from Ancient
        //     to Monster — which would shift the entire type-walk
        //     by one row and land us on the wrong node.
        RetryContext.IsRetrying = true;
        RetryContext.SkipNextNeowEntry = true;
        try
        {
            // FromSerializable requires every act to have a non-null,
            // fully-populated SerializableRoomSet (boss id, events,
            // encounters) — which we can't reconstruct from history
            // because the finished-run RunHistory doesn't store room
            // composition. So we use the new-run pipeline instead:
            //   • StartNewSingleplayerRun creates a fresh RunState
            //     with the same seed and character, then generates
            //     rooms / map exactly like a normal new game.
            //   • InjectInventory swaps the freshly-rolled starting
            //     inventory for the reconstructed historical one,
            //     non-silently so the HUD picks it up.
            //   • If the target is on a later act, we hand off to
            //     RunManager.SetActInternal which advances and
            //     regenerates the map for that act.
            //   • Finally, we walk the new map matching the historical
            //     MapPointType per row to find the target coord, then
            //     EnterMapCoord drops the player into that room.
            // Mid-run retries are handled upstream in Begin(): the
            // abandon modal must run + complete (calling CleanUp +
            // DeleteCurrentRun) BEFORE we reach this point. If a run
            // is still in progress here, something skipped the modal.
            if (RunManager.Instance.IsInProgress)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}LaunchAsync: run still in progress — modal/cleanup skipped?");
                return;
            }
            if (NGame.Instance == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}LaunchAsync: NGame.Instance null");
                return;
            }

            var character = ModelDb.GetById<MegaCrit.Sts2.Core.Models.CharacterModel>(target.Player.CharacterId ?? ModelId.none);
            var acts = new List<MegaCrit.Sts2.Core.Models.ActModel>(target.ActIds.Count);
            foreach (var id in target.ActIds)
            {
                acts.Add(ModelDb.GetById<MegaCrit.Sts2.Core.Models.ActModel>(id));
            }
            var modifiers = new List<MegaCrit.Sts2.Core.Models.ModifierModel>(target.Modifiers.Count);
            foreach (var m in target.Modifiers)
            {
                try { modifiers.Add(MegaCrit.Sts2.Core.Models.ModifierModel.FromSerializable(m)); }
                catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}skip modifier: {ex.Message}"); }
            }

            try
            {
                await NGame.Instance.Transition.FadeOut(0.8f, character.CharacterSelectTransitionPath);
            }
            catch { /* aesthetic */ }

            try { NAudioManager.Instance?.StopMusic(); } catch { }

            // Preserve progression eligibility when replaying an unmodified
            // Standard history entry. The game only awards ascension
            // progression to Standard runs, so forcing every retry to Custom
            // prevented single-player history retries from unlocking the next
            // ascension level. Custom, daily, and modified histories remain
            // Custom so they cannot accidentally gain Standard progression.
            var retryGameMode = target.GameMode == GameMode.Standard
                && target.Modifiers.Count == 0
                    ? GameMode.Standard
                    : GameMode.Custom;
            var runState = await NGame.Instance.StartNewSingleplayerRun(
                character, shouldSave: true, acts, modifiers,
                target.Seed, retryGameMode, target.Ascension);

            // EnterAct(0) inside StartRun has already fired by now;
            // any later EnterMapCoord(StartingMapPoint) is OURS (e.g.
            // when the target IS the StartingMapPoint of a later act
            // like act 1's Ancient). Clear the suppress flag so the
            // patch doesn't eat our explicit call.
            RetryContext.SkipNextNeowEntry = false;

            GD.Print($"{RetryMod.LogPrefix}post-StartNew: rngSeedString={runState.Rng.StringSeed} rngSeedUint={runState.Rng.Seed} currentAct={runState.CurrentActIndex} mapNull={runState.Map == null} startedNeow={runState.ExtraFields.StartedWithNeow}");
            DebugDump.DumpMap(runState);

            // Inject reconstructed inventory non-silently so the
            // relic / potion HUD picks up the changes. For act 0
            // this is fine post-launch because none of act 1's
            // relics participate in Hook.ModifyGeneratedMap. Later
            // acts where the player has GoldenCompass / FurCoat are
            // an edge case we'll handle separately.
            try { InventoryInjector.Apply(runState, target.Player); }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}inject inventory: {ex.Message}\n{ex.StackTrace}");
            }

            // Offset the in-run timer so a retry doesn't read as
            // "0:00" — we want the speedrun-timer display to roughly
            // reflect where the original run was time-wise. Pro-rate
            // total RunTime by floor position; the historical death
            // node gets the full original time.
            try { RunTimeOffset.Apply(target); }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}offset timer: {ex.Message}");
            }

            // RNG counter restoration happens later, inside
            // NavigateToTarget, once we've resolved the actual target
            // coord — the snapshot is keyed by coord and only that
            // call can find it.

            // Jump to target act if not already on it. SetActInternal
            // clears visited coords, resets ActFloor, and regenerates
            // the map for the new act.
            if (target.TargetActIndex > 0 && target.TargetActIndex < runState.Acts.Count)
            {
                try
                {
                    await RunManager.Instance.SetActInternal(target.TargetActIndex);
                    DebugDump.DumpMap(runState);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}SetActInternal: {ex.Message}");
                }
            }

            // Walk the new map matching the historical MapPointType
            // chain to locate the target coord, then enter that room.
            // Map generation is seeded so the new map matches the
            // historical layout exactly — the walk should always find
            // a unique path. If it doesn't (modded maps, edge cases),
            // FindTargetNode falls back to "any child with matching
            // type, else first child".
            try
            {
                await NavigateToTarget(runState, target);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}navigate to target: {ex.Message}\n{ex.StackTrace}");
            }

            try { await NGame.Instance.Transition.FadeIn(); } catch { }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}LaunchAsync: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            RetryContext.IsRetrying = false;
            RetryContext.SkipNextNeowEntry = false;
        }
    }

    // Walk the freshly-generated act map matching the historical
    // MapPointType sequence to find the target node, mark the path
    // visited so the map screen shows a coherent trail, then enter
    // the target room.
    //
    // Map structure: StartingMapPoint (row 0) → row 1 nodes → ... →
    // row N → Boss. Children of each node lead to row+1 nodes. We
    // greedily pick the child whose PointType matches the next
    // history entry; if multiple match we pick the first one
    // enumerated. With the same seed, the new map's structure is
    // identical to the recorded run's, so the type sequence uniquely
    // identifies a path on well-formed runs.
    private static async Task NavigateToTarget(RunState runState, RetryTarget target)
    {
        var map = runState.Map;
        if (map == null)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}navigate: no map");
            return;
        }

        // Pull the act's history list. May be missing entries (older
        // save shapes); guard accordingly.
        IReadOnlyList<MapPointHistoryEntry>? actHistory = null;
        if (target.MapPointHistorySoFar.Count > target.TargetActIndex)
        {
            actHistory = target.MapPointHistorySoFar[target.TargetActIndex];
        }
        if (actHistory == null || actHistory.Count == 0)
        {
            GD.Print($"{RetryMod.LogPrefix}navigate: no act history — landing at act start");
            return;
        }

        // Build the chain of MapPointType for floors 0..targetFloor.
        // targetFloor is inclusive — we want to END on that entry.
        int targetFloor = Math.Min(target.TargetFloorIndex, actHistory.Count - 1);
        var walkNotes = new List<string>();
        walkNotes.Add($"targetFloor={targetFloor} actHistoryCount={actHistory.Count}");

        // Try the constraint-satisfying DFS first — given the new
        // map is a deterministic regen of the original, this finds
        // the exact path matching the historical type sequence (if
        // unique) or any path matching it (if ambiguous).
        var wantedTypes = new List<MapPointType>(targetFloor + 1);
        for (int i = 0; i <= targetFloor && i < actHistory.Count; i++)
        {
            wantedTypes.Add(actHistory[i].MapPointType);
        }

        var exactHints = new Dictionary<int, MapCoord>();
        var legacyHints = new Dictionary<int, MapCoord>();
        for (int floor = 0; floor <= targetFloor; floor++)
        {
            var exact = RngSnapshotStore.TryGetExactCoord(
                target.SourceStartTime, target.Seed, target.TargetActIndex, floor);
            if (exact.HasValue) exactHints[floor] = exact.Value;
            var legacy = RngSnapshotStore.TryGetLegacyCoord(
                target.Seed, target.TargetActIndex, floor);
            if (legacy.HasValue) legacyHints[floor] = legacy.Value;
        }

        if (exactHints.Count == wantedTypes.Count)
        {
            var coords = Enumerable.Range(0, wantedTypes.Count).Select(i => exactHints[i]).ToList();
            if (MapPathSearch.TryBuildCoordinatePath(map, coords, out var coordinatePath, out var coordinateFailure))
            {
                walkNotes.Add($"v3 coords: validated path of {coordinatePath.Count} coords");
                DebugDump.DumpWalk(coordinatePath, walkNotes);
                await EnterAtPath(runState, target, coordinatePath, dfsExact: true);
                return;
            }
            walkNotes.Add($"v3 coords: rejected ({coordinateFailure})");
        }

        var dfsPath = MapPathSearch.FindPath(map, wantedTypes);
        if (dfsPath != null && dfsPath.Count > 0)
        {
            walkNotes.Add($"dfs: found path of {dfsPath.Count} coords");
            foreach (var mp in dfsPath) walkNotes.Add($"  ({mp.coord.row},{mp.coord.col},{mp.PointType})");
            DebugDump.DumpWalk(dfsPath, walkNotes);
            await EnterAtPath(runState, target, dfsPath, dfsExact: true);
            return;
        }
        walkNotes.Add("dfs: no exact-type path found, trying closest complete legal path");

        var closest = MapPathSearch.FindClosestPath(map, wantedTypes, exactHints, legacyHints);
        if (closest != null && closest.Path.Count > 0 && closest.Complete)
        {
            walkNotes.Add(
                $"closest: length={closest.Path.Count}/{wantedTypes.Count} " +
                $"typeMismatches={closest.TypeMismatches} exactCoordMatches={closest.ExactCoordinateMatches} " +
                $"legacyCoordMatches={closest.LegacyCoordinateMatches} complete={closest.Complete}");
            foreach (var mp in closest.Path)
                walkNotes.Add($"  ({mp.coord.row},{mp.coord.col},{mp.PointType})");
            DebugDump.DumpWalk(closest.Path, walkNotes);
            await EnterAtPath(runState, target, closest.Path, dfsExact: false);
            return;
        }
        if (closest != null)
            walkNotes.Add($"closest: incomplete path {closest.Path.Count}/{wantedTypes.Count}; refusing target navigation");
        walkNotes.Add("closest: no legal path found, falling back to greedy walk");

        // Determine whether history's first entry corresponds to the
        // StartingMapPoint or to a row-1 node. Act 0 with Neow makes
        // the first entry the Ancient; other entries start at the
        // first content room (row 1).
        var path = new List<MapPoint>();
        MapPoint current = map.StartingMapPoint;
        int historyCursor = 0;
        walkNotes.Add($"start=row{current.coord.row},col{current.coord.col},type={current.PointType} vs history[0]={actHistory[historyCursor].MapPointType}");
        if (current.PointType == actHistory[historyCursor].MapPointType)
        {
            // History entry [0] = StartingMapPoint itself.
            path.Add(current);
            historyCursor++;
            walkNotes.Add($"matched starting map point as history[0]");
        }

        // Helper: if a snapshot recorded the exact (row, col) the
        // original player visited at this floor, prefer that coord —
        // it disambiguates between siblings of the same type. Only
        // accept coords that are actually a child of `current` and
        // match the expected type, so a stale snapshot can't put us
        // somewhere the live graph doesn't connect to.
        MapPoint? PickFromSnapshot(MapPoint cur, MapPointType wantType, int floor)
        {
            var snapCoord = RngSnapshotStore.TryGetCoord(
                target.SourceStartTime, target.Seed, target.TargetActIndex, floor);
            if (snapCoord == null) return null;
            foreach (var c in cur.Children)
            {
                if (c.coord.row == snapCoord.Value.row && c.coord.col == snapCoord.Value.col)
                {
                    if (c.PointType != wantType)
                    {
                        walkNotes.Add($"  snapshot coord ({snapCoord.Value.row},{snapCoord.Value.col}) type {c.PointType} != want {wantType}; ignoring");
                        return null;
                    }
                    walkNotes.Add($"  snapshot hit: ({c.coord.row},{c.coord.col}) type={c.PointType}");
                    return c;
                }
            }
            walkNotes.Add($"  snapshot coord ({snapCoord.Value.row},{snapCoord.Value.col}) not a child of ({cur.coord.row},{cur.coord.col}); ignoring");
            return null;
        }

        while (historyCursor <= targetFloor && historyCursor < actHistory.Count)
        {
            var wantType = actHistory[historyCursor].MapPointType;
            MapPoint? next = null;
            var orderedChildren = current.Children.OrderBy(c => c.coord.col).ThenBy(c => c.coord.row).ToList();
            var childSummary = string.Join(",", orderedChildren.Select(c => $"({c.coord.row},{c.coord.col},{c.PointType})"));
            walkNotes.Add($"floor={historyCursor} from=({current.coord.row},{current.coord.col}) want={wantType} children=[{childSummary}]");

            // 1) Prefer the snapshot's recorded coord for this floor.
            next = PickFromSnapshot(current, wantType, historyCursor);
            // 2) Fall back to first child matching the type.
            if (next == null)
            {
                foreach (var child in orderedChildren)
                {
                    if (child.PointType == wantType) { next = child; break; }
                }
                if (next != null) walkNotes.Add($"  type match: ({next.coord.row},{next.coord.col})");
            }
            // 3) Last resort: first child regardless of type.
            if (next == null && orderedChildren.Count > 0)
            {
                walkNotes.Add($"  no type match → fallback to first child");
                next = orderedChildren[0];
            }
            if (next == null) { walkNotes.Add("  no children → break"); break; }
            walkNotes.Add($"  chose=({next.coord.row},{next.coord.col},{next.PointType})");
            path.Add(next);
            current = next;
            historyCursor++;
        }
        DebugDump.DumpWalk(path, walkNotes);

        if (path.Count == 0)
        {
            GD.Print($"{RetryMod.LogPrefix}navigate: empty path — landing at act start");
            DebugDump.DumpWalk(path, walkNotes);
            return;
        }
        DebugDump.DumpWalk(path, walkNotes);
        await EnterAtPath(runState, target, path, dfsExact: false);
    }

    // Shared post-pathfind step: mark intermediate coords visited,
    // pre-populate history, align events, simulate queues, apply
    // snapshot, then enter the target room. Used by both the DFS
    // (preferred) and the greedy-walk fallback.
    private static async Task EnterAtPath(RunState runState, RetryTarget target, List<MapPoint> path, bool dfsExact)
    {
        // Intermediate coords get marked visited — paints the map
        // path but doesn't fire rooms (we already injected the
        // historical inventory).
        for (int i = 0; i < path.Count - 1; i++)
        {
            try { runState.AddVisitedMapCoord(path[i].coord); }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}mark visited {path[i].coord.row},{path[i].coord.col}: {ex.Message}"); }
        }

        try { HistoryPrePopulator.Apply(runState, target); }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}history prepop: {ex.Message}"); }

        // Visited events are run-global state, not part of SerializablePlayer.
        // Restore completed acts directly; current-act events are added by the
        // queue simulation below so its eventsVisited counter advances too.
        try { VisitedEventSeeder.SeedCompletedActs(runState, target); }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}seed visited events: {ex.Message}"); }

        try { EventListPatcher.AlignToHistory(runState, target); }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}align events: {ex.Message}"); }

        try { RoomQueueSimulator.SimulatePriorRooms(runState, target); }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}simulate queues: {ex.Message}"); }

        var targetCoord = path[path.Count - 1].coord;
        try
        {
            long destinationStartTime = RngSnapshotStore.GetCurrentRunStartTime();
            for (int act = 0; act < target.TargetActIndex
                && act < target.MapPointHistorySoFar.Count; act++)
            {
                RngSnapshotStore.CopyExactCoordinates(
                    target.SourceStartTime, destinationStartTime, target.Seed,
                    act, target.MapPointHistorySoFar[act].Count);
            }
            RngSnapshotStore.CopyExactCoordinates(
                target.SourceStartTime,
                destinationStartTime,
                target.Seed,
                target.TargetActIndex,
                target.TargetFloorIndex + 1);
            RngSnapshotStore.CaptureCoordinates(
                destinationStartTime, target.Seed, target.TargetActIndex,
                path.Select(point => point.coord).ToList());
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}seed retry coordinates: {ex.Message}");
        }

        bool snapshotApplied = false;
        try
        {
            snapshotApplied = RngSnapshotStore.TryApplyLive(
                runState.Rng, target.SourceStartTime, target.Seed,
                target.TargetActIndex, target.TargetFloorIndex);
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}snapshot apply: {ex.Message}"); }

        // Seed the destination run identity with the fully restored room-entry
        // state. This also makes re-retrying a reconstructed legacy history
        // exact from this point onward.
        try
        {
            long destinationStartTime = RngSnapshotStore.GetCurrentRunStartTime();
            RngSnapshotStore.Capture(
                destinationStartTime,
                runState.Rng.StringSeed,
                target.TargetActIndex,
                target.TargetFloorIndex,
                targetCoord,
                runState.Rng.ToSerializable().Counters,
                runState.Players.Select(player => player.ToSerializable()).ToList());
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot seed restored state: {ex.Message}");
        }

        // If the target's MapPointType is Unknown, force the
        // historical RoomType. Otherwise the live UnknownMapPoint
        // odds may roll a different concrete type (e.g. an
        // Unknown→Monster in history becomes Unknown→Event in retry).
        bool roomTypeForced = false;
        if (target.MapPointHistorySoFar.Count > target.TargetActIndex
            && target.MapPointHistorySoFar[target.TargetActIndex].Count > target.TargetFloorIndex)
        {
            var hentry = target.MapPointHistorySoFar[target.TargetActIndex][target.TargetFloorIndex];
            if (hentry.MapPointType == MapPointType.Unknown && hentry.Rooms != null && hentry.Rooms.Count > 0)
            {
                RetryContext.TargetExpectedRoomType = hentry.Rooms[0].RoomType;
                roomTypeForced = true;
                GD.Print($"{RetryMod.LogPrefix}override Unknown→{hentry.Rooms[0].RoomType} for target");
            }
            // For non-Unknown mismatches (MP map ≠ SP-regenerated
            // map at this coord — see notes on MP retry RNG drift),
            // mutate the live MapPoint's PointType so EnterMapCoord
            // creates the historical room type. RollRoomTypeFor only
            // fires for Unknown points, so for concrete types we
            // have to set the field directly. Writes to the auto-
            // property's backing field via reflection.
            try
            {
                var livePoint = path[path.Count - 1];
                var historicalType = hentry.MapPointType;
                if (historicalType != MapPointType.Unknown
                    && livePoint.PointType != historicalType)
                {
                    var fld = livePoint.GetType().GetField("<PointType>k__BackingField",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (fld != null)
                    {
                        var before = livePoint.PointType;
                        fld.SetValue(livePoint, historicalType);
                        GD.Print($"{RetryMod.LogPrefix}override target coord ({targetCoord.row},{targetCoord.col}) PointType {before} → {historicalType}");
                    }
                    else
                    {
                        GD.PrintErr($"{RetryMod.LogPrefix}override target: <PointType>k__BackingField not found on {livePoint.GetType().Name}");
                    }
                }
            }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}override target type: {ex.Message}"); }
            // Stash historical card-reward offer so the post-combat
            // reward shows the same 3 cards the original player got.
            // PlayerStats indexing: the entry's PlayerStats list
            // typically has one element per player; the original
            // player's id should be in target.Player.NetId.
            try
            {
                foreach (var ps in hentry.PlayerStats)
                {
                    if (target.Player.NetId != 0 && ps.PlayerId != target.Player.NetId) continue;
                    if (ps.CardChoices != null && ps.CardChoices.Count > 0)
                    {
                        var offered = new List<MegaCrit.Sts2.Core.Saves.Runs.SerializableCard>(ps.CardChoices.Count);
                        foreach (var cc in ps.CardChoices) offered.Add(cc.Card);
                        RetryContext.TargetCardChoices = offered;
                        GD.Print($"{RetryMod.LogPrefix}prime card-reward: {string.Join(",", offered.Select(c => c.Id?.ToString() ?? "?"))}");
                    }
                    if (ps.RelicChoices != null)
                    {
                        foreach (var rc in ps.RelicChoices)
                        {
                            if (rc.wasPicked && rc.choice != ModelId.none)
                            {
                                RetryContext.TargetExpectedRelic = rc.choice;
                                GD.Print($"{RetryMod.LogPrefix}prime relic-reward: {rc.choice}");
                                break;
                            }
                        }
                    }
                    break;
                }
            }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}prime rewards: {ex.Message}"); }
        }

        GD.Print($"{RetryMod.LogPrefix}navigate: entering target coord row={targetCoord.row} col={targetCoord.col} type={path[path.Count - 1].PointType} (snapshot={(snapshotApplied ? "applied" : "miss")})");
        try
        {
            await RunManager.Instance.EnterMapCoord(targetCoord);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}EnterMapCoord: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            RetryContext.TargetExpectedRoomType = null;
            // Don't clear TargetCardChoices here — it's consumed
            // post-combat by CardReward.Populate, which fires after
            // we've already returned from EnterMapCoord. The patch
            // clears it itself on first fire.
        }

        // EnterMapCoord has now built the room AND the in-run HUD
        // (NRun + GlobalUi). At this point we can safely rebuild
        // the HUD's relic / deck visuals from the live player
        // state. Doing it AFTER EnterMapCoord avoids race
        // conditions where RelicInventory.Initialize fired before
        // our injection events landed.
        try { HudResync.Apply(runState); }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}hud-resync wrap: {ex.Message}"); }

        try
        {
            FidelityReport.Emit(new FidelityReport.Inputs
            {
                Target = target,
                RunState = runState,
                LandedCoord = targetCoord,
                SnapshotApplied = snapshotApplied,
                DfsPathFound = dfsExact,
                EventListAligned = true,
                RoomTypeForced = roomTypeForced,
            });
            DebugDump.DumpVisited(runState);
        }
        catch { /* informational only */ }
    }

    // Walk RunHistory's nested list-of-acts to find the (actIndex,
    // floorIndex) of a specific entry instance. The entry object
    // identity is what the screen handed us so reference-equality
    // is correct here.
    private static (int actIndex, int floorIndex) LocateInHistory(
        RunHistory history, MapPointHistoryEntry entry)
    {
        for (int a = 0; a < history.MapPointHistory.Count; a++)
        {
            var act = history.MapPointHistory[a];
            for (int f = 0; f < act.Count; f++)
            {
                if (ReferenceEquals(act[f], entry)) return (a, f);
            }
        }
        return (-1, -1);
    }

    private static int SumFloors(RunHistory history)
    {
        int total = 0;
        foreach (var act in history.MapPointHistory) total += act.Count;
        return total;
    }

    // Inclusive of the target floor — the walk needs the target's
    // type to land on the right column, the simulator and pre-
    // populator clip to floors strictly before target themselves.
    private static List<List<MapPointHistoryEntry>> TruncateHistory(
        RunHistory history, int targetActIndex, int targetFloorIndex)
    {
        var result = new List<List<MapPointHistoryEntry>>(history.MapPointHistory.Count);
        for (int a = 0; a < history.MapPointHistory.Count; a++)
        {
            var act = history.MapPointHistory[a];
            if (a > targetActIndex) break;
            int take = (a == targetActIndex) ? targetFloorIndex + 1 : act.Count;
            var copy = new List<MapPointHistoryEntry>(take);
            for (int f = 0; f < take && f < act.Count; f++) copy.Add(act[f]);
            result.Add(copy);
        }
        return result;
    }
}
