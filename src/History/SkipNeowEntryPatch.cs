// Skip the one auto-EnterMapCoord on StartingMapPoint that happens
// during EnterAct(0) when StartedWithNeow=true. We can't simply
// clear the flag earlier — RunManager.GenerateMap reads it and, if
// false, rewrites StartingMapPoint.PointType from Ancient to Monster.
// That shifts the type sequence and breaks the retry walk by one
// row. Leaving the flag set keeps the map identical to the original
// run; we just intercept the single Neow-entry call right before it
// would open the Ancient event room.
//
// Once we've suppressed that one entry, subsequent EnterMapCoord
// calls (e.g. our own EnterMapCoord(targetCoord)) pass through
// untouched. The flag is cleared inside the patch so the second
// retry from the same launch wouldn't re-suppress (defensive — we
// only ever expect one retry per launch).
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterMapCoord))]
public static class RunManager_EnterMapCoord_Patch
{
    static bool Prefix(RunManager __instance, MapCoord coord, ref Task __result)
    {
        if (!RetryMod.Enabled)
            return true;
        if (!RetryContext.IsRetrying)
            return true;
        if (!RetryContext.SkipNextNeowEntry)
            return true;
        var state = StateAccessor.GetState(__instance);
        if (state?.Map?.StartingMapPoint == null)
            return true;
        var startCoord = state.Map.StartingMapPoint.coord;
        if (coord.row != startCoord.row || coord.col != startCoord.col)
            return true;
        // Mark visited so the walk and the map UI treat the Ancient
        // as historically completed (it is — original run picked from
        // it; our injected relics already reflect that pick).
        state.AddVisitedMapCoord(coord);
        RetryContext.SkipNextNeowEntry = false;
        __result = Task.CompletedTask;
        return false;
    }
}
