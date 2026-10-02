// Snapshot the run's RunRngSet counters every time the player
// enters a new map point. Without this, retry from old runs is
// resource-faithful but the shared Shuffle / CombatCardSelection /
// etc. streams replay from counter=0, giving different opening
// hands. Snapshotting at the *start* of each room means a retry
// from that node restarts the run with the exact counter values
// the original combat saw.
//
// Hook: RunState.AddVisitedMapCoord is the choke point for "we just
// arrived at this coord". A Postfix gets us both the new coord and
// access to RunState (and thus RunRngSet).
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

[HarmonyPatch(typeof(RunState), nameof(RunState.AddVisitedMapCoord))]
public static class RunState_AddVisitedMapCoord_Patch
{
    static void Postfix(RunState __instance, MapCoord coord, bool __result)
    {
        // __result is false when the coord was already in the list
        // (e.g. resume from save replays the call) — skip those to
        // avoid overwriting a richer earlier snapshot with a stale
        // re-entry.
        if (!__result) return;
        if (!RetryMod.Enabled) return;
        // Don't capture while a retry is mid-launch. Our NavigateToTarget
        // marks intermediate coords as visited just to paint the map
        // path — those coords didn't come from a real playthrough, so
        // their RNG state is wrong and capturing them pollutes the store
        // for future retries.
        if (RetryContext.IsRetrying) return;
        try
        {
            var rng = __instance.Rng;
            var counters = rng.ToSerializable().Counters;
            int floor = __instance.MapPointHistory.Count > __instance.CurrentActIndex
                ? __instance.MapPointHistory[__instance.CurrentActIndex].Count
                : 0;
            long startTime = RngSnapshotStore.GetCurrentRunStartTime();
            if (startTime <= 0)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}snapshot capture skipped: run start time unavailable");
                return;
            }
            RngSnapshotStore.Capture(
                startTime, rng.StringSeed, __instance.CurrentActIndex, floor, coord, counters);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot capture: {ex.Message}");
        }
    }
}
