// Side-channel diagnostics for the debug loop. Dumps the generated
// act map (all coords, types, child connections), the historical
// path, and the walk decisions to JSON files in user:// so the
// debugger (the agent) can diff them against expectations without
// having to parse the godot.log stream.
//
// Files written:
//   user://retry_the_spire_debug_map.json      — newest generated map
//   user://retry_the_spire_debug_history.json  — original run's path/types
//   user://retry_the_spire_debug_walk.json     — walk decisions per row
using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace Retry;

public static class DebugDump
{
    public static void DumpMap(RunState runState)
    {
        try
        {
            if (runState?.Map == null) return;
            var map = runState.Map;
            var rows = new Godot.Collections.Array();
            var sm = map.StartingMapPoint;
            foreach (var mp in map.GetAllMapPoints())
            {
                var d = new Godot.Collections.Dictionary
                {
                    ["row"] = mp.coord.row,
                    ["col"] = mp.coord.col,
                    ["type"] = mp.PointType.ToString(),
                    ["children"] = ChildArray(mp),
                };
                rows.Add(d);
            }
            var outer = new Godot.Collections.Dictionary
            {
                ["seed_string"] = runState.Rng.StringSeed,
                ["seed_uint"] = runState.Rng.Seed,
                ["act_index"] = runState.CurrentActIndex,
                ["act_id"] = runState.Act?.Id.ToString() ?? "?",
                ["players_count"] = runState.Players.Count,
                ["row_count"] = map.GetRowCount(),
                ["col_count"] = map.GetColumnCount(),
                ["starting_map_point"] = new Godot.Collections.Dictionary {
                    ["row"] = sm.coord.row,
                    ["col"] = sm.coord.col,
                    ["type"] = sm.PointType.ToString(),
                    ["children"] = ChildArray(sm),
                },
                ["boss_map_point"] = new Godot.Collections.Dictionary {
                    ["row"] = map.BossMapPoint.coord.row,
                    ["col"] = map.BossMapPoint.coord.col,
                    ["type"] = map.BossMapPoint.PointType.ToString(),
                },
                ["nodes"] = rows,
            };
            Write("retry_the_spire_debug_map.json", Json.Stringify(outer, "  "));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}DebugDump.DumpMap: {ex.Message}");
        }
    }

    public static void DumpHistoryPath(RunHistory history, int act, int floor)
    {
        try
        {
            if (act >= history.MapPointHistory.Count) return;
            var entries = history.MapPointHistory[act];
            var arr = new Godot.Collections.Array();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var rooms = new Godot.Collections.Array();
                if (e.Rooms != null)
                {
                    foreach (var r in e.Rooms)
                    {
                        rooms.Add(new Godot.Collections.Dictionary {
                            ["room_type"] = r.RoomType.ToString(),
                            ["model_id"] = r.ModelId?.ToString() ?? "",
                        });
                    }
                }
                arr.Add(new Godot.Collections.Dictionary {
                    ["floor"] = i,
                    ["map_point_type"] = e.MapPointType.ToString(),
                    ["rooms"] = rooms,
                });
            }
            var outer = new Godot.Collections.Dictionary
            {
                ["seed"] = history.Seed,
                ["act"] = act,
                ["target_floor"] = floor,
                ["entries"] = arr,
            };
            Write("retry_the_spire_debug_history.json", Json.Stringify(outer, "  "));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}DebugDump.DumpHistoryPath: {ex.Message}");
        }
    }

    public static void DumpVisited(RunState runState)
    {
        try
        {
            if (runState == null) return;
            var arr = new Godot.Collections.Array();
            foreach (var c in runState.VisitedMapCoords)
            {
                arr.Add(new Godot.Collections.Dictionary { ["row"] = c.row, ["col"] = c.col });
            }
            var outer = new Godot.Collections.Dictionary
            {
                ["act_index"] = runState.CurrentActIndex,
                ["act_floor"] = runState.ActFloor,
                ["visited_count"] = arr.Count,
                ["visited"] = arr,
            };
            Write("retry_the_spire_debug_visited.json", Json.Stringify(outer, "  "));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}DebugDump.DumpVisited: {ex.Message}");
        }
    }

    public static void DumpWalk(List<MapPoint> path, List<string> notes)
    {
        try
        {
            var nodes = new Godot.Collections.Array();
            foreach (var mp in path)
            {
                nodes.Add(new Godot.Collections.Dictionary {
                    ["row"] = mp.coord.row,
                    ["col"] = mp.coord.col,
                    ["type"] = mp.PointType.ToString(),
                });
            }
            var notesArr = new Godot.Collections.Array();
            foreach (var n in notes) notesArr.Add(n);
            var outer = new Godot.Collections.Dictionary
            {
                ["path"] = nodes,
                ["notes"] = notesArr,
            };
            Write("retry_the_spire_debug_walk.json", Json.Stringify(outer, "  "));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}DebugDump.DumpWalk: {ex.Message}");
        }
    }

    private static Godot.Collections.Array ChildArray(MapPoint mp)
    {
        var arr = new Godot.Collections.Array();
        foreach (var c in mp.Children)
        {
            arr.Add(new Godot.Collections.Dictionary {
                ["row"] = c.coord.row,
                ["col"] = c.coord.col,
                ["type"] = c.PointType.ToString(),
            });
        }
        return arr;
    }

    private static void Write(string name, string content)
    {
        try
        {
            using var f = FileAccess.Open("user://" + name, FileAccess.ModeFlags.Write);
            if (f == null) { GD.PrintErr($"{RetryMod.LogPrefix}DebugDump.Write: cannot open {name}"); return; }
            f.StoreString(content);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}DebugDump.Write {name}: {ex.Message}");
        }
    }
}
