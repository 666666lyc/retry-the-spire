using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace Retry;

internal static class HistoryNavigation
{
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
    internal static async Task NavigateToTarget(RunState runState, RetryTarget target)
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
            var exact = RngSnapshotStore.TryGetExactCoord(target.SourceStartTime, target.Seed, target.TargetActIndex, floor);
            if (exact.HasValue)
                exactHints[floor] = exact.Value;
            var legacy = RngSnapshotStore.TryGetLegacyCoord(target.Seed, target.TargetActIndex, floor);
            if (legacy.HasValue)
                legacyHints[floor] = legacy.Value;
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
            foreach (var mp in dfsPath)
                walkNotes.Add($"  ({mp.coord.row},{mp.coord.col},{mp.PointType})");
            DebugDump.DumpWalk(dfsPath, walkNotes);
            await EnterAtPath(runState, target, dfsPath, dfsExact: true);
            return;
        }

        walkNotes.Add("dfs: no exact-type path found, trying closest complete legal path");
        var closest = MapPathSearch.FindClosestPath(map, wantedTypes, exactHints, legacyHints);
        if (closest != null && closest.Path.Count > 0 && closest.Complete)
        {
            walkNotes.Add($"closest: length={closest.Path.Count}/{wantedTypes.Count} " + $"typeMismatches={closest.TypeMismatches} exactCoordMatches={closest.ExactCoordinateMatches} " + $"legacyCoordMatches={closest.LegacyCoordinateMatches} complete={closest.Complete}");
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
            var snapCoord = RngSnapshotStore.TryGetCoord(target.SourceStartTime, target.Seed, target.TargetActIndex, floor);
            if (snapCoord == null)
                return null;
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
                    if (child.PointType == wantType)
                    {
                        next = child;
                        break;
                    }
                }

                if (next != null)
                    walkNotes.Add($"  type match: ({next.coord.row},{next.coord.col})");
            }

            // 3) Last resort: first child regardless of type.
            if (next == null && orderedChildren.Count > 0)
            {
                walkNotes.Add($"  no type match → fallback to first child");
                next = orderedChildren[0];
            }

            if (next == null)
            {
                walkNotes.Add("  no children → break");
                break;
            }

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
    internal static async Task EnterAtPath(RunState runState, RetryTarget target, List<MapPoint> path, bool dfsExact)
    {
        // When an old/corrupt sidecar has no map blob, keep every visited
        // historical node exact even though unvisited branches came from the
        // current generator. This is intentionally done for the whole path,
        // not just the target room.
        if (target.MapPointHistorySoFar.Count > target.TargetActIndex)
        {
            int normalized = MapPathSearch.NormalizeHistoricalTypes(path, target.MapPointHistorySoFar[target.TargetActIndex]);
            if (normalized > 0)
                GD.PrintErr($"{RetryMod.LogPrefix}fallback map: normalized {normalized} visited node types");
        }

        // Intermediate coords get marked visited — paints the map
        // path but doesn't fire rooms (we already injected the
        // historical inventory).
        for (int i = 0; i < path.Count - 1; i++)
        {
            try
            {
                runState.AddVisitedMapCoord(path[i].coord);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}mark visited {path[i].coord.row},{path[i].coord.col}: {ex.Message}");
            }
        }

        try
        {
            HistoryPrePopulator.Apply(runState, target);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}history prepop: {ex.Message}");
        }

        // Visited events are run-global state, not part of SerializablePlayer.
        // Restore completed acts directly; current-act events are added by the
        // queue simulation below so its eventsVisited counter advances too.
        try
        {
            VisitedEventSeeder.SeedCompletedActs(runState, target);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}seed visited events: {ex.Message}");
        }

        try
        {
            EventListPatcher.AlignToHistory(runState, target);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}align events: {ex.Message}");
        }

        try
        {
            RoomQueueSimulator.SimulatePriorRooms(runState, target);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}simulate queues: {ex.Message}");
        }

        var targetCoord = path[path.Count - 1].coord;
        try
        {
            long destinationStartTime = RngSnapshotStore.GetCurrentRunStartTime();
            for (int act = 0; act < target.TargetActIndex && act < target.MapPointHistorySoFar.Count; act++)
            {
                RngSnapshotStore.CopyExactCoordinates(target.SourceStartTime, destinationStartTime, target.Seed, act, target.MapPointHistorySoFar[act].Count);
            }

            RngSnapshotStore.CopyExactCoordinates(target.SourceStartTime, destinationStartTime, target.Seed, target.TargetActIndex, target.TargetFloorIndex + 1);
            RngSnapshotStore.CaptureCoordinates(destinationStartTime, target.Seed, target.TargetActIndex, path.Select(point => point.coord).ToList());
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}seed retry coordinates: {ex.Message}");
        }

        bool snapshotApplied = false;
        try
        {
            snapshotApplied = RngSnapshotStore.TryApplyLive(runState.Rng, target.SourceStartTime, target.Seed, target.TargetActIndex, target.TargetFloorIndex);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot apply: {ex.Message}");
        }

        // Seed the destination run identity with the fully restored room-entry
        // state. This also makes re-retrying a reconstructed legacy history
        // exact from this point onward.
        try
        {
            long destinationStartTime = RngSnapshotStore.GetCurrentRunStartTime();
            RngSnapshotStore.Capture(destinationStartTime, runState.Rng.StringSeed, target.TargetActIndex, target.TargetFloorIndex, targetCoord, runState.Rng.ToSerializable().Counters, runState.Players.Select(player => player.ToSerializable()).ToList());
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
        if (target.MapPointHistorySoFar.Count > target.TargetActIndex && target.MapPointHistorySoFar[target.TargetActIndex].Count > target.TargetFloorIndex)
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
                if (historicalType != MapPointType.Unknown && livePoint.PointType != historicalType)
                {
                    var fld = livePoint.GetType().GetField("<PointType>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
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
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}override target type: {ex.Message}");
            }

            // Stash historical card-reward offer so the post-combat
            // reward shows the same 3 cards the original player got.
            // PlayerStats indexing: the entry's PlayerStats list
            // typically has one element per player; the original
            // player's id should be in target.Player.NetId.
            try
            {
                foreach (var ps in hentry.PlayerStats)
                {
                    if (target.Player.NetId != 0 && ps.PlayerId != target.Player.NetId)
                        continue;
                    if (ps.CardChoices != null && ps.CardChoices.Count > 0)
                    {
                        var offered = new List<MegaCrit.Sts2.Core.Saves.Runs.SerializableCard>(ps.CardChoices.Count);
                        foreach (var cc in ps.CardChoices)
                            offered.Add(cc.Card);
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
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}prime rewards: {ex.Message}");
            }
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
        try
        {
            HudResync.Apply(runState);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}hud-resync wrap: {ex.Message}");
        }

        try
        {
            FidelityReport.Emit(new FidelityReport.Inputs { Target = target, RunState = runState, LandedCoord = targetCoord, SnapshotApplied = snapshotApplied, DfsPathFound = dfsExact, EventListAligned = true, RoomTypeForced = roomTypeForced, });
            DebugDump.DumpVisited(runState);
        }
        catch
        { /* informational only */
        }

        try
        {
            RetryRunner.OnRetryCompleted(runState, target);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}retry completion observer: {ex.Message}");
        }
    }
}
