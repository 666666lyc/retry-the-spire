// Advance the act's room queues to reflect the historical path the
// player took before reaching the retry target. Without this, the
// freshly-started run's encounter / event queues are at index 0 →
// the target room is created with the first encounter from the
// queue (i.e. the floor-1 monster) instead of the floor-N monster
// the original player actually fought.
//
// How the queues work (RoomSet.cs):
//   normalEncounters[normalEncountersVisited % count]  ← next monster
//   eliteEncounters [eliteEncountersVisited  % count]  ← next elite
//   events          [eventsVisited           % count]  ← next event
//   Boss / SecondBoss tracked by bossEncountersVisited (binary-ish)
//
// All counters are incremented by ActModel.MarkRoomVisited when the
// player enters a base-level room. So advancing the queues is just
// calling MarkRoomVisited the right number of times.
//
// Events are slightly different: PullNextEvent() runs
// EnsureNextEventIsValid which skips over already-visited events
// (advancing eventsVisited until it lands on an unvisited one) and
// adds the chosen event to RunState.VisitedEventIds. Just bumping
// the counter doesn't replicate the "skip visited" logic, so for
// events we call PullNextEvent() then MarkRoomVisited — that matches
// what the original CreateRoom + EnterRoom path does. The Ancient
// event (floor 0 of act 0) uses PullAncient() which doesn't advance
// any counter, so we skip it entirely.
using System;
using Godot;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Map;

namespace Retry;

public static class RoomQueueSimulator
{
    public static void SimulatePriorRooms(RunState runState, RetryTarget target)
    {
        if (target.MapPointHistorySoFar.Count <= target.TargetActIndex)
            return;
        var actHistory = target.MapPointHistorySoFar[target.TargetActIndex];
        if (actHistory.Count == 0)
            return;
        int targetFloor = Math.Min(target.TargetFloorIndex, actHistory.Count);
        // Snap the act model to the act we're simulating queues for.
        // The target act may not be the current act if we're retrying
        // into a later act; RetryRunner calls SetActInternal beforehand
        // which flips CurrentActIndex, so runState.Act is correct here.
        var act = runState.Act;
        GD.Print($"{RetryMod.LogPrefix}sim start: targetFloor={targetFloor} actHistoryCount={actHistory.Count}");
        for (int floor = 0; floor < targetFloor; floor++)
        {
            var entry = actHistory[floor];
            if (entry.MapPointType == MapPointType.Ancient)
            {
                GD.Print($"{RetryMod.LogPrefix}sim floor {floor}: Ancient → skip");
                continue;
            }

            if (entry.Rooms == null || entry.Rooms.Count == 0)
                continue;
            var primary = entry.Rooms[0];
            try
            {
                switch (primary.RoomType)
                {
                    case RoomType.Event:
                        var evt = act.PullNextEvent(runState);
                        act.MarkRoomVisited(RoomType.Event);
                        GD.Print($"{RetryMod.LogPrefix}sim floor {floor}: Event pulled={evt?.Id}");
                        VerifyMatch(floor, primary.ModelId?.ToString(), evt?.Id.ToString(), "Event");
                        break;
                    case RoomType.Monster:
                    case RoomType.Elite:
                    case RoomType.Boss:
                        var enc = act.PullNextEncounter(primary.RoomType);
                        act.MarkRoomVisited(primary.RoomType);
                        GD.Print($"{RetryMod.LogPrefix}sim floor {floor}: {primary.RoomType} next-encounter={enc?.Id}");
                        VerifyMatch(floor, primary.ModelId?.ToString(), enc?.Id.ToString(), primary.RoomType.ToString());
                        break;
                    case RoomType.Shop:
                    case RoomType.Treasure:
                    case RoomType.RestSite:
                        // No encounter queue, but RunState tracks per-type
                        // visit counts (NumOfShops gates shop relic rarity
                        // / pool depth, treasure & rest similarly). Without
                        // this, retrying into the act-3 shop generates as
                        // if it were the act-1 shop — the same relic the
                        // player already bought re-rolls in.
                        act.MarkRoomVisited(primary.RoomType);
                        GD.Print($"{RetryMod.LogPrefix}sim floor {floor}: {primary.RoomType} (counter-only)");
                        break;
                    default:
                        GD.Print($"{RetryMod.LogPrefix}sim floor {floor}: {primary.RoomType} (no queue)");
                        break;
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}simulate floor {floor} ({primary.RoomType}): {ex.Message}");
            }
        }

        // Final state — what would PullNext return now? Encounter
        // pulls are pure reads (no state mutation); we DON'T probe
        // PullNextEvent here because it has side effects (advances
        // eventsVisited via EnsureNextEventIsValid + AddVisitedEvent).
        try
        {
            GD.Print($"{RetryMod.LogPrefix}sim end: next-monster={act.PullNextEncounter(RoomType.Monster)?.Id} next-elite={act.PullNextEncounter(RoomType.Elite)?.Id}");
        }
        catch
        { /* informational only */
        }
    }

    private static void VerifyMatch(int floor, string? historicalId, string? simulatedId, string kind)
    {
        if (string.IsNullOrEmpty(historicalId) || string.IsNullOrEmpty(simulatedId))
            return;
        if (historicalId == simulatedId)
            return;
        GD.PrintErr($"{RetryMod.LogPrefix}DRIFT floor {floor} {kind}: historical={historicalId} simulated={simulatedId} — encounter pool likely differs (mod update? unlock change?)");
    }
}
