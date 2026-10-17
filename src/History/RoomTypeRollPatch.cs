// Override `RollRoomTypeFor` for the SINGLE retry-target's Unknown
// node so it resolves to the historical RoomType, not whatever the
// live UnknownMapPoint odds would produce.
//
// Why this is needed: when the target's MapPointType is Unknown
// (~unmarked node), EnterMapPointInternal asks RollRoomTypeFor to
// pick a concrete RoomType — Event, Monster, Treasure, Shop —
// using State.Odds.UnknownMapPoint. The Odds state at retry time
// doesn't match what it was at the original visit, so the roll
// lands differently. The historical RunHistory tells us exactly
// what the original resolved to (entry.Rooms[0].RoomType), so we
// just force that.
//
// We only override when:
//   1. RetryContext.IsRetrying is true
//   2. RetryContext.TargetExpectedRoomType is set
//   3. The argument pointType is Unknown
// Each launch sets TargetExpectedRoomType once before EnterMapCoord
// and clears it after.
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

[HarmonyPatch(typeof(RunManager), "RollRoomTypeFor")]
public static class RunManager_RollRoomTypeFor_Patch
{
    static bool Prefix(MapPointType pointType, ref RoomType __result)
    {
        if (!RetryMod.Enabled)
            return true;
        if (!RetryContext.IsRetrying)
            return true;
        if (!RetryContext.TargetExpectedRoomType.HasValue)
            return true;
        if (pointType != MapPointType.Unknown)
            return true;
        __result = RetryContext.TargetExpectedRoomType.Value;
        // Don't clear here — the original method may not run if we
        // skip it, and the same call resolves to one RoomType. Clear
        // on the launch side in RetryRunner once EnterMapCoord
        // returns.
        return false;
    }
}
