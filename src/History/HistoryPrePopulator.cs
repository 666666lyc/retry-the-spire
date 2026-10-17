// Seed RunState.MapPointHistory with the historical entries for
// floors 0..target-1 so the in-run history view shows the path the
// player took to reach the retry node.
//
// Cross-player compatibility: in multiplayer runs each entry's
// PlayerStats list holds one PlayerMapPointHistoryEntry per Steam
// ID. The live SP runState created by StartNewSingleplayerRun has a
// single player whose Id is usually 1 (not the user's Steam ID).
// Once we hand control back to the game, EnterMapCoord calls
// MapPointHistoryEntry.GetEntry(player.Id=1) on the historical
// entries we copied in — and the game throws because no PlayerStats
// row exists for Id 1, only the historical Steam IDs.
//
// Fix: clone each entry on the way in, drop everyone except the
// selected historical player, and rewrite that lone stats row's
// PlayerId to the live SP runState's player.Id. SP retries (where
// the historical Steam ID already matches the live player) still
// work — the filter passes through, the rewrite is a no-op when
// ids match.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace Retry;

public static class HistoryPrePopulator
{
    private static readonly FieldInfo? MapHistoryField = typeof(RunState).GetField("_mapPointHistory", BindingFlags.NonPublic | BindingFlags.Instance);
    public static void Apply(RunState runState, RetryTarget target)
    {
        if (MapHistoryField == null)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}history prepop: _mapPointHistory field not found");
            return;
        }

        if (MapHistoryField.GetValue(runState)is not List<List<MapPointHistoryEntry>> live)
        {
            return;
        }

        if (runState.Players.Count == 0)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}history prepop: runState has no players");
            return;
        }

        ulong livePlayerId = runState.Players[0].NetId;
        ulong selectedHistoricalId = target.Player.NetId;
        for (int act = 0; act <= target.TargetActIndex && act < target.MapPointHistorySoFar.Count; act++)
        {
            while (live.Count <= act)
                live.Add(new List<MapPointHistoryEntry>());
            var sourceAct = target.MapPointHistorySoFar[act];
            int floorsToCopy = (act == target.TargetActIndex) ? Math.Min(target.TargetFloorIndex, sourceAct.Count) : sourceAct.Count;
            // Replace whatever's in the live list — there may already
            // be a partial entry for the auto-Neow path we suppressed.
            live[act].Clear();
            for (int floor = 0; floor < floorsToCopy; floor++)
            {
                var cloned = CloneEntryForLivePlayer(sourceAct[floor], selectedHistoricalId, livePlayerId);
                if (cloned != null)
                    live[act].Add(cloned);
            }
        }
    }

    // Shallow-clone the entry, replacing its PlayerStats with a
    // single-entry list whose PlayerId matches the live SP player.
    // Returns null if the selected historical player isn't present
    // in the source entry (shouldn't happen, but defensive).
    private static MapPointHistoryEntry? CloneEntryForLivePlayer(MapPointHistoryEntry source, ulong selectedHistoricalId, ulong livePlayerId)
    {
        try
        {
            var stats = source.PlayerStats?.FirstOrDefault(ps => ps != null && ps.PlayerId == selectedHistoricalId);
            if (stats == null)
            {
                // Multiplayer entry where the selected player wasn't
                // present this floor — fall back to whatever single
                // stats row exists, rewritten. Still produces a usable
                // entry for the game's GetEntry lookup.
                stats = source.PlayerStats?.FirstOrDefault();
                if (stats == null)
                    return null;
            }

            var entryClone = ShallowClone<MapPointHistoryEntry>(source);
            if (entryClone == null)
                return null;
            var statsClone = ShallowClone<PlayerMapPointHistoryEntry>(stats);
            if (statsClone == null)
                return null;
            statsClone.PlayerId = livePlayerId;
            entryClone.PlayerStats = new List<PlayerMapPointHistoryEntry>
            {
                statsClone
            };
            return entryClone;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}clone entry: {ex.Message}");
            return null;
        }
    }

    // Reflection-based shallow clone. Copies public + non-public
    // writable properties; inner collections are shared by
    // reference (cards_gained, relic_choices, etc. — none of those
    // get mutated during the retry, so reference sharing is safe).
    private static T? ShallowClone<T>(T source)
        where T : class, new()
    {
        try
        {
            var clone = new T();
            foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite)
                    continue;
                try
                {
                    prop.SetValue(clone, prop.GetValue(source));
                }
                catch
                { /* skip un-settable / indexer properties */
                }
            }

            return clone;
        }
        catch
        {
            return null;
        }
    }
}
