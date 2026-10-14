using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

// RunManager.GenerateMap already has the game's canonical saved-map branch.
// Supplying the snapshot here preserves all native load hooks and guarantees
// the map is queued after run setup but before the branch is evaluated.
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateMap))]
public static class RunManager_GenerateMap_SavedSnapshot_Patch
{
    static void Prefix(RunManager __instance)
    {
        if (!RetryContext.IsRetrying
            || RetryContext.SourceStartTime <= 0
            || string.IsNullOrEmpty(RetryContext.SourceSeed))
            return;
        try
        {
            var state = StateAccessor.GetState(__instance);
            if (state == null) return;
            int actIndex = state.CurrentActIndex;
            if (!RngSnapshotStore.QueueSavedMapForGeneration(
                    RetryContext.SourceStartTime,
                    RetryContext.SourceSeed,
                    actIndex))
            {
                GD.PrintErr(
                    $"{RetryMod.LogPrefix}warning: exact map snapshot unavailable for act={actIndex}; " +
                    "regenerating unvisited branches and preserving the recorded path");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}queue saved map: {ex.Message}");
        }
    }
}
