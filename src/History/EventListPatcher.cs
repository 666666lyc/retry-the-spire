// Force the act's _rooms.events list to start with the events the
// original player visited, in the original order. Then PullNextEvent
// (and its EnsureNextEventIsValid skip-visited logic) naturally
// pulls the historical events as we walk the path.
//
// Why this is needed: ActModel.GenerateRooms filters events by the
// current player's unlock state before shuffling. If the player
// unlocked more event-epochs between the original run and the
// retry, the filtered list is longer and the shuffle order differs
// → PullNextEvent returns a different event at floor N than the
// original had. We can't recreate the historical UnlockState (it's
// not stored in RunHistory), but we CAN reorder the post-shuffle
// list deterministically based on the events the history records.
//
// We only reorder events that appear in BOTH the historical visit
// list AND the live `_rooms.events`. Events visited historically
// but not present in the current `_rooms.events` (e.g. modded
// events removed since) are skipped — PullNextEvent will pull
// something else and combat-equivalence is lost for that node.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

public static class EventListPatcher
{
    private static readonly FieldInfo? RoomsField = typeof(ActModel).GetField("_rooms", BindingFlags.NonPublic | BindingFlags.Instance);
    public static void AlignToHistory(RunState runState, RetryTarget target)
    {
        if (RoomsField == null)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}event-align: _rooms field missing");
            return;
        }

        if (target.MapPointHistorySoFar.Count <= target.TargetActIndex)
            return;
        var actHistory = target.MapPointHistorySoFar[target.TargetActIndex];
        if (actHistory.Count == 0)
            return;
        var act = runState.Act;
        if (act == null)
            return;
        if (RoomsField.GetValue(act)is not RoomSet roomSet)
            return;
        if (roomSet.events == null || roomSet.events.Count == 0)
            return;
        // Build the ordered list of historical event ids we expect to
        // pull from this act's event queue. Ancient floor (Neow) is
        // PullAncient — not from the events list — so exclude it.
        var historicalIds = new List<string>();
        foreach (var entry in actHistory)
        {
            if (entry.MapPointType == MapPointType.Ancient)
                continue;
            if (entry.Rooms == null || entry.Rooms.Count == 0)
                continue;
            var primary = entry.Rooms[0];
            if (primary.RoomType != RoomType.Event)
                continue;
            var id = primary.ModelId;
            if (id != null)
                historicalIds.Add(id.ToString());
        }

        if (historicalIds.Count == 0)
        {
            GD.Print($"{RetryMod.LogPrefix}event-align: no historical events before target");
            return;
        }

        var current = roomSet.events;
        var byId = new Dictionary<string, List<EventModel>>();
        foreach (var e in current)
        {
            var k = e.Id.ToString();
            if (!byId.TryGetValue(k, out var list))
            {
                list = new List<EventModel>();
                byId[k] = list;
            }

            list.Add(e);
        }

        var newOrder = new List<EventModel>(current.Count);
        var picked = new HashSet<EventModel>();
        foreach (var id in historicalIds)
        {
            if (byId.TryGetValue(id, out var list) && list.Count > 0)
            {
                var pick = list[0];
                list.RemoveAt(0);
                newOrder.Add(pick);
                picked.Add(pick);
            }
            else
            {
                GD.Print($"{RetryMod.LogPrefix}event-align: historical event {id} not in current event list — will fall back");
            }
        }

        foreach (var e in current)
        {
            if (!picked.Contains(e))
                newOrder.Add(e);
        }

        try
        {
            roomSet.events.Clear();
            roomSet.events.AddRange(newOrder);
            GD.Print($"{RetryMod.LogPrefix}event-align: reordered. front={string.Join(",", newOrder.Take(Math.Min(5, newOrder.Count)).Select(e => e.Id.ToString()))}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}event-align write: {ex.Message}");
        }
    }
}
