using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

internal static class HistoryTargetBuilder
{
    // Walk RunHistory's nested list-of-acts to find the (actIndex,
    // floorIndex) of a specific entry instance. The entry object
    // identity is what the screen handed us so reference-equality
    // is correct here.
    internal static (int actIndex, int floorIndex) LocateInHistory(RunHistory history, MapPointHistoryEntry entry)
    {
        for (int a = 0; a < history.MapPointHistory.Count; a++)
        {
            var act = history.MapPointHistory[a];
            for (int f = 0; f < act.Count; f++)
            {
                if (ReferenceEquals(act[f], entry))
                    return (a, f);
            }
        }

        return (-1, -1);
    }

    internal static int SumFloors(RunHistory history)
    {
        int total = 0;
        foreach (var act in history.MapPointHistory)
            total += act.Count;
        return total;
    }

    // Inclusive of the target floor — the walk needs the target's
    // type to land on the right column, the simulator and pre-
    // populator clip to floors strictly before target themselves.
    internal static List<List<MapPointHistoryEntry>> TruncateHistory(RunHistory history, int targetActIndex, int targetFloorIndex)
    {
        var result = new List<List<MapPointHistoryEntry>>(history.MapPointHistory.Count);
        for (int a = 0; a < history.MapPointHistory.Count; a++)
        {
            var act = history.MapPointHistory[a];
            if (a > targetActIndex)
                break;
            int take = (a == targetActIndex) ? targetFloorIndex + 1 : act.Count;
            var copy = new List<MapPointHistoryEntry>(take);
            for (int f = 0; f < take && f < act.Count; f++)
                copy.Add(act[f]);
            result.Add(copy);
        }

        return result;
    }

    internal static RetryTarget Build(RunHistory history, RunHistoryPlayer player, int actIndex, int floorIndex)
    {
        var snapshot = StateReconstructor.ReconstructAtTarget(history, actIndex, floorIndex, player);
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
        return target;
    }
}
