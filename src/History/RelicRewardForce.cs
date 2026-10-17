// Force the target room's relic reward to match the historical
// pick. Without this, treasure rooms, elite drops, and boss drops
// roll from the relic grab bag (RNG-driven) and give a different
// relic than the original.
//
// Why patch `RelicGrabBag.PullFromFront` and not
// `RelicFactory.PullNextRelicFromFront`: the factory is used by
// `RelicReward.Populate` (elite, boss, etc.) but
// `TreasureRoomRelicSynchronizer.BeginRelicPicking` reaches into
// `_sharedGrabBag.PullFromFront(...)` directly, bypassing the
// factory. Patching the grab bag method catches BOTH paths in one
// place. One-shot — clears the context after the first fire so
// later relic rolls in the same retry session are unaffected.
//
// We also remove the relic from the live grab bag (mirroring what
// the original `PullFromFront` does on success) so future rolls
// don't offer it again.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

[HarmonyPatch(typeof(RelicGrabBag), nameof(RelicGrabBag.PullFromFront), typeof(RelicRarity), typeof(Func<RelicModel, bool>), typeof(IRunState))]
public static class RelicGrabBag_PullFromFront_Patch
{
    static bool Prefix(RelicGrabBag __instance, ref RelicModel? __result)
    {
        if (!RetryMod.Enabled)
            return true;
        var id = RetryContext.TargetExpectedRelic;
        if (id == null)
            return true;
        var canonical = ModelDb.GetByIdOrNull<RelicModel>(id);
        if (canonical == null)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}force relic: id {id} not in ModelDb");
            return true;
        }

        try
        {
            __instance.Remove(canonical);
        }
        catch (Exception ex)
        {
            GD.Print($"{RetryMod.LogPrefix}force relic remove from grab bag: {ex.Message} (continuing)");
        }

        RetryContext.TargetExpectedRelic = null;
        // Return the canonical model — _currentRelics holds canonical
        // refs, and the treasure room's AnimateRelicAwards later calls
        // `.ToMutable()` itself (which asserts canonical input). If we
        // pre-mutate, that downstream call throws MutableModelException
        // and the relic never animates into the inventory bar.
        __result = canonical;
        GD.Print($"{RetryMod.LogPrefix}forced relic: {id}");
        return false;
    }
}
