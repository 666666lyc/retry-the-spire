// Restores run-global event history for acts completed before the retry
// target. Current-act events are deliberately left to RoomQueueSimulator:
// PullNextEvent must consume them in order so the act's event counter stays
// aligned with the historical run.
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

public static class VisitedEventSeeder
{
    public static void SeedCompletedActs(RunState runState, RetryTarget target)
    {
        int restored = 0;
        for (int act = 0;
             act < target.TargetActIndex && act < target.MapPointHistorySoFar.Count;
             act++)
        {
            foreach (var entry in target.MapPointHistorySoFar[act])
            {
                foreach (var room in entry.Rooms)
                {
                    if (room.RoomType != RoomType.Event || room.ModelId == null)
                        continue;

                    var model = ModelDb.GetByIdOrNull<EventModel>(room.ModelId);
                    if (model == null)
                    {
                        GD.PrintErr(
                            $"{RetryMod.LogPrefix}DRIFT visited event {room.ModelId} " +
                            "not in ModelDb — skipping");
                        continue;
                    }

                    int before = runState.VisitedEventIds.Count;
                    runState.AddVisitedEvent(model);
                    if (runState.VisitedEventIds.Count > before) restored++;
                }
            }
        }

        GD.Print(
            $"{RetryMod.LogPrefix}visited events: restored={restored} " +
            $"completedActs={target.TargetActIndex} total={runState.VisitedEventIds.Count}");
    }
}
