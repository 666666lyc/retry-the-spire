// Summarize, at the end of a retry launch, which parts of the
// reconstruction were "perfect" vs "approximate". A retry can land
// at the right node and pull the right encounter but still differ
// from the original in combat-internal state — opening hand,
// monster AI rolls, card rewards — when the RNG snapshot for the
// target floor doesn't exist (e.g. the player hasn't replayed this
// seed with the mod installed). Spelling out which knobs hit and
// which fell back to defaults makes it obvious when a retry is
// faithful and when the user should accept divergence.
//
// One line per category, then a one-line verdict. The line lives
// in godot.log; future versions could surface it in the in-game
// HUD via a transient banner.
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace Retry;

public static class FidelityReport
{
    public sealed class Inputs
    {
        public RetryTarget Target = null !;
        public RunState RunState = null !;
        public MapCoord LandedCoord;
        public bool SnapshotApplied;
        public bool DfsPathFound;
        public bool EventListAligned;
        public bool RoomTypeForced;
        public string? Notes;
    }

    public static void Emit(Inputs i)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append($"{RetryMod.LogPrefix}fidelity: target=(act{i.Target.TargetActIndex},floor{i.Target.TargetFloorIndex},type=");
            var entry = GetTargetEntry(i.Target);
            sb.Append(entry?.MapPointType.ToString() ?? "?");
            sb.Append(") landed=(");
            sb.Append($"{i.LandedCoord.row},{i.LandedCoord.col})");
            sb.Append($" path={(i.DfsPathFound ? "DFS-exact" : "greedy-fallback")}");
            sb.Append($" events={(i.EventListAligned ? "aligned" : "as-rolled")}");
            sb.Append($" snapshot={(i.SnapshotApplied ? "APPLIED" : "miss")}");
            if (i.RoomTypeForced)
                sb.Append(" room-type=forced");
            if (i.Notes != null)
                sb.Append($" notes={i.Notes}");
            // Verdict: an "exact" retry needs DFS-exact path + snapshot
            // applied. Anything less is "approximate" — the room
            // structure and encounter lineup match, but combat-internal
            // RNG (shuffles, draws, monster AI) and card rewards may
            // diverge from the original.
            sb.Append("\n").Append($"{RetryMod.LogPrefix}fidelity-verdict: ");
            if (i.DfsPathFound && i.SnapshotApplied)
                sb.Append("EXACT — opening hand and combat draws should match the original");
            else if (i.DfsPathFound && !i.SnapshotApplied)
                sb.Append("APPROXIMATE — map/encounter match, but combat RNG diverges (no snapshot for this floor; play the original seed with the mod installed to capture snapshots)");
            else if (!i.DfsPathFound)
                sb.Append("FALLBACK — map type sequence didn't match exactly; walk used greedy fallback. Likely a modded run or live map differs from history");
            GD.Print(sb.ToString());
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}FidelityReport: {ex.Message}");
        }
    }

    private static MapPointHistoryEntry? GetTargetEntry(RetryTarget t)
    {
        if (t.MapPointHistorySoFar.Count <= t.TargetActIndex)
            return null;
        var act = t.MapPointHistorySoFar[t.TargetActIndex];
        if (act.Count <= t.TargetFloorIndex)
            return null;
        return act[t.TargetFloorIndex];
    }
}
