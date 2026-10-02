// Constraint-satisfying DFS to recover the historical path through
// the new map. Map gen is seeded so the new map's graph and node
// types are identical to the original run's. Each historical floor
// records a MapPointType; if we line them up against the map, only
// one (or a few) coord chains satisfy the type sequence from
// StartingMapPoint to target.
//
// The greedy "pick leftmost matching child at each row" approach
// fails whenever a row has multiple matching candidates and the
// downstream path only routes through a specific one. Example
// observed in TBESFXYS6G act 0 retry-to-floor-12: row 5 has
// Monster at cols 2 and 6; greedy picks col 2 but only col 6's
// subtree reaches (12,5,Unknown) matching history. Backtracking
// finds the right pick.
//
// Search behavior:
//   • If history[0] is Ancient, start from StartingMapPoint as
//     row 0 and recurse on its children for row 1.
//   • Otherwise (act > 0 or act 0 without Neow), the first wanted
//     type is a row-1 type and we recurse on each StartingMapPoint
//     child individually.
//   • At each step the candidate's PointType must equal
//     wanted[idx]. First (column-sorted) match wins; backtrack on
//     dead end. Stops when idx == wanted.Count - 1.
//
// If no path exactly satisfies the type sequence (e.g. modded
// content swapped a type), the caller falls back to the older
// greedy walk.
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Map;

namespace Retry;

public static class MapPathSearch
{
    public sealed class BestEffortResult
    {
        public required List<MapPoint> Path;
        public int ExactCoordinateMatches;
        public int LegacyCoordinateMatches;
        public int TypeMismatches;
        public bool Complete;
    }

    public static List<MapPoint>? FindPath(ActMap map, IReadOnlyList<MapPointType> wanted)
    {
        if (wanted.Count == 0) return null;
        var startingMapPoint = map.StartingMapPoint;
        var path = new List<MapPoint>();

        if (startingMapPoint.PointType == wanted[0])
        {
            path.Add(startingMapPoint);
            if (wanted.Count == 1) return path;
            if (Recurse(startingMapPoint, wanted, 1, path)) return path;
            return null;
        }

        // history doesn't start with Ancient → first wanted is a
        // row-1 type. Try each StartingMapPoint child as the entry
        // point for the search.
        foreach (var child in startingMapPoint.Children.OrderBy(c => c.coord.col).ThenBy(c => c.coord.row))
        {
            path.Clear();
            if (child.PointType != wanted[0]) continue;
            path.Add(child);
            if (wanted.Count == 1) return path;
            if (Recurse(child, wanted, 1, path)) return path;
        }
        return null;
    }

    private static bool Recurse(MapPoint cur, IReadOnlyList<MapPointType> wanted, int idx, List<MapPoint> path)
    {
        var want = wanted[idx];
        foreach (var child in cur.Children.OrderBy(c => c.coord.col).ThenBy(c => c.coord.row))
        {
            if (child.PointType != want) continue;
            path.Add(child);
            if (idx == wanted.Count - 1) return true;
            if (Recurse(child, wanted, idx + 1, path)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    public static bool TryBuildCoordinatePath(
        ActMap map,
        IReadOnlyList<MapCoord> coords,
        out List<MapPoint> path,
        out string? failure)
    {
        path = new List<MapPoint>();
        failure = null;
        if (coords.Count == 0)
        {
            failure = "coordinate list is empty";
            return false;
        }

        var allPoints = map.GetAllMapPoints().ToList();
        if (!allPoints.Contains(map.StartingMapPoint))
            allPoints.Add(map.StartingMapPoint);

        MapPoint? current = allPoints.FirstOrDefault(p => SameCoord(p.coord, coords[0]));
        if (current == null)
        {
            failure = $"floor 0 coordinate ({coords[0].row},{coords[0].col}) is absent from the regenerated map";
            return false;
        }
        if (current != map.StartingMapPoint
            && !map.StartingMapPoint.Children.Contains(current))
        {
            failure = $"floor 0 coordinate ({coords[0].row},{coords[0].col}) is not a legal map entry";
            return false;
        }
        path.Add(current);

        for (int i = 1; i < coords.Count; i++)
        {
            var wantedCoord = coords[i];
            var next = current.Children.FirstOrDefault(p => SameCoord(p.coord, wantedCoord));
            if (next == null)
            {
                failure = $"floor {i} coordinate ({wantedCoord.row},{wantedCoord.col}) is not a child of "
                    + $"({current.coord.row},{current.coord.col})";
                path.Clear();
                return false;
            }
            path.Add(next);
            current = next;
        }
        return true;
    }

    public static BestEffortResult? FindClosestPath(
        ActMap map,
        IReadOnlyList<MapPointType> wanted,
        IReadOnlyDictionary<int, MapCoord>? exactCoordinateHints = null,
        IReadOnlyDictionary<int, MapCoord>? legacyCoordinateHints = null)
    {
        if (wanted.Count == 0) return null;

        var memo = new Dictionary<(MapPoint Point, int Index), Candidate>();
        Candidate Walk(MapPoint point, int index)
        {
            if (memo.TryGetValue((point, index), out var cached)) return cached;

            var best = new Candidate
            {
                Path = new List<MapPoint> { point },
                ExactMatches = HintMatches(exactCoordinateHints, index, point.coord) ? 1 : 0,
                LegacyMatches = HintMatches(legacyCoordinateHints, index, point.coord) ? 1 : 0,
                TypeMismatches = point.PointType == wanted[index] ? 0 : 1,
            };

            if (index + 1 < wanted.Count)
            {
                foreach (var child in point.Children.OrderBy(p => p.coord.col).ThenBy(p => p.coord.row))
                {
                    var tail = Walk(child, index + 1);
                    var candidate = new Candidate
                    {
                        Path = new List<MapPoint>(tail.Path.Count + 1) { point },
                        ExactMatches = HintMatches(exactCoordinateHints, index, point.coord) ? 1 : 0,
                        LegacyMatches = HintMatches(legacyCoordinateHints, index, point.coord) ? 1 : 0,
                        TypeMismatches = point.PointType == wanted[index] ? 0 : 1,
                    };
                    candidate.Path.AddRange(tail.Path);
                    candidate.ExactMatches += tail.ExactMatches;
                    candidate.LegacyMatches += tail.LegacyMatches;
                    candidate.TypeMismatches += tail.TypeMismatches;
                    if (IsBetter(candidate, best)) best = candidate;
                }
            }

            memo[(point, index)] = best;
            return best;
        }

        var roots = new List<MapPoint>();
        bool startsAtMapStart = wanted[0] == MapPointType.Ancient
            || HintMatches(exactCoordinateHints, 0, map.StartingMapPoint.coord)
            || map.StartingMapPoint.PointType == wanted[0];
        if (startsAtMapStart)
        {
            roots.Add(map.StartingMapPoint);
        }
        else
        {
            roots.AddRange(map.StartingMapPoint.Children
                .OrderBy(p => p.coord.col).ThenBy(p => p.coord.row));
        }

        Candidate? winner = null;
        foreach (var root in roots)
        {
            var candidate = Walk(root, 0);
            if (winner == null || IsBetter(candidate, winner)) winner = candidate;
        }
        if (winner == null) return null;

        return new BestEffortResult
        {
            Path = winner.Path,
            ExactCoordinateMatches = winner.ExactMatches,
            LegacyCoordinateMatches = winner.LegacyMatches,
            TypeMismatches = winner.TypeMismatches,
            Complete = winner.Path.Count == wanted.Count,
        };
    }

    private sealed class Candidate
    {
        public required List<MapPoint> Path;
        public int ExactMatches;
        public int LegacyMatches;
        public int TypeMismatches;
    }

    private static bool IsBetter(Candidate candidate, Candidate current)
    {
        if (candidate.Path.Count != current.Path.Count)
            return candidate.Path.Count > current.Path.Count;
        if (candidate.ExactMatches != current.ExactMatches)
            return candidate.ExactMatches > current.ExactMatches;
        if (candidate.TypeMismatches != current.TypeMismatches)
            return candidate.TypeMismatches < current.TypeMismatches;
        if (candidate.LegacyMatches != current.LegacyMatches)
            return candidate.LegacyMatches > current.LegacyMatches;
        return false;
    }

    private static bool HintMatches(
        IReadOnlyDictionary<int, MapCoord>? hints,
        int index,
        MapCoord coord)
        => hints != null && hints.TryGetValue(index, out var hint) && SameCoord(hint, coord);

    private static bool SameCoord(MapCoord a, MapCoord b)
        => a.row == b.row && a.col == b.col;
}
