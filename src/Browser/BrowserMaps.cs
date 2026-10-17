using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace Retry;

// Owns maps operations for a browser session.
internal sealed class BrowserMaps
{
    private readonly BrowserSession _session;
    internal BrowserMaps(BrowserSession session) => _session = session;
    internal bool PrepareRunState()
    {
        if (_session._history == null || _session._player == null)
            return false;
        // For MP runs we must build the runState with one Player per
        // historical participant — map gen branches on
        // `runState.Players.Count > 1` (StandardActMap.cs:96), so a
        // single-player runState produces an SP map that the historical
        // MP path can never satisfy. SP runs stick with the legacy
        // NetId=1uL convention; MP uses the recorded Steam-style ids.
        bool isMp = _session._history.Players.Count > 1;
        var unlockState = MegaCrit.Sts2.Core.Unlocks.UnlockState.all;
        var players = new List<MegaCrit.Sts2.Core.Entities.Players.Player>();
        if (isMp)
        {
            foreach (var hp in _session._history.Players)
            {
                var cm = ModelDb.GetByIdOrNull<CharacterModel>(hp.Character);
                if (cm == null)
                    continue;
                players.Add(MegaCrit.Sts2.Core.Entities.Players.Player.CreateForNewRun(cm, unlockState, hp.Id));
            }
        }

        if (players.Count == 0)
        {
            var fallbackChar = ModelDb.GetByIdOrNull<CharacterModel>(_session._player.Character);
            if (fallbackChar == null)
                return false;
            players.Add(MegaCrit.Sts2.Core.Entities.Players.Player.CreateForNewRun(fallbackChar, unlockState, 1uL));
        }

        var actsList = new List<ActModel>();
        foreach (var id in _session._history.Acts)
            actsList.Add(ModelDb.GetById<ActModel>(id).ToMutable());
        _session._runState = RunState.CreateForNewRun(players, actsList, new List<ModifierModel>(), _session._history.GameMode, _session._history.Ascension, _session._history.Seed);
        // Populate _mapPointHistory from the saved history so
        // NMapPoint.OnFocus → GetHistoryEntryFor returns real data
        // and the hover tooltip (NMapPointHistoryHoverTip) displays
        // floor/encounter/reward info on visited nodes.
        var historyField = typeof(RunState).GetField("_mapPointHistory", BindingFlags.Instance | BindingFlags.NonPublic);
        if (historyField?.GetValue(_session._runState)is List<List<MapPointHistoryEntry>> mph)
        {
            mph.Clear();
            foreach (var act in _session._history.MapPointHistory)
            {
                mph.Add(new List<MapPointHistoryEntry>(act));
            }
        }

        // Snapshot the pre-browser LocalContext.NetId so Close can
        // restore it.
        _session._savedLocalNetId = MegaCrit.Sts2.Core.Context.LocalContext.NetId;
        // For MP runs, override what the SP NetService reports as its
        // NetId. RunManager.InitializeShared and Launch both copy
        // NetService.NetId into LocalContext.NetId, so if we don't
        // override the getter here, LocalContext.GetMe(playerCollection)
        // looks for player with NetId=1 but our MP players have Steam
        // IDs → throws. Patched at NetSingleplayerGameService_NetId_Patch.
        if (_session._history.Players.Count > 1 && _session._player != null)
            NActMapBrowser.NetServiceNetIdOverride = _session._player.Id;
        else
            NActMapBrowser.NetServiceNetIdOverride = null;
        var rm = RunManager.Instance;
        if (!rm.IsInProgress)
        {
            rm.SetUpNewSingleplayer(_session._runState, shouldSave: false);
        }

        return true;
    }

    internal void OverrideLocalNetId()
    {
        if (_session._history != null && _session._history.Players.Count > 1 && _session._player != null)
        {
            MegaCrit.Sts2.Core.Context.LocalContext.NetId = _session._player.Id;
            GD.Print($"{RetryMod.LogPrefix}browser LocalContext.NetId override → {_session._player.Id}");
        }
    }

    internal void PrecomputeActs()
    {
        if (_session._history == null || _session._runState == null)
            return;
        int n = _session._history.MapPointHistory.Count;
        _session._actMaps = new ActMap? [n];
        _session._actVisited = new List<MapCoord>[n];
        for (int i = 0; i < n; i++)
        {
            try
            {
                var(map, visited) = ReconstructAct(i);
                _session._actMaps[i] = map;
                _session._actVisited[i] = visited;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}browser precompute act {i}: {ex.Message}");
            }
        }
    }

    internal (ActMap? map, List<MapCoord> visited) ReconstructAct(int actIdx)
    {
        if (_session._history == null || _session._runState == null)
            return (null, new());
        if (actIdx >= _session._history.Acts.Count)
            return (null, new());
        SetCurrentActIndex(actIdx);
        var act = _session._runState.Acts[actIdx];
        var map = RngSnapshotStore.TryCreateSavedActMap(_session._history.StartTime, _session._history.Seed, actIdx);
        bool restoredExactMap = map != null;
        if (map == null)
        {
            map = act.CreateMap(_session._runState, replaceTreasureWithElites: false);
            GD.PrintErr($"{RetryMod.LogPrefix}browser warning: exact map snapshot unavailable for act={actIdx}; " + "unvisited branches are regenerated");
        }
        else
        {
            GD.Print($"{RetryMod.LogPrefix}browser act {actIdx}: restored exact native map snapshot");
        }

        var entries = _session._history.MapPointHistory[actIdx];
        var typeChain = new List<MapPointType>();
        foreach (var e in entries)
            typeChain.Add(e.MapPointType);
        var exactHints = new Dictionary<int, MapCoord>();
        var legacyHints = new Dictionary<int, MapCoord>();
        for (int floor = 0; floor < entries.Count; floor++)
        {
            var exact = RngSnapshotStore.TryGetExactCoord(_session._history.StartTime, _session._history.Seed, actIdx, floor);
            if (exact.HasValue)
                exactHints[floor] = exact.Value;
            var legacy = RngSnapshotStore.TryGetLegacyCoord(_session._history.Seed, actIdx, floor);
            if (legacy.HasValue)
                legacyHints[floor] = legacy.Value;
        }

        List<MapPoint>? path = null;
        if (exactHints.Count == entries.Count)
        {
            var coords = Enumerable.Range(0, entries.Count).Select(i => exactHints[i]).ToList();
            if (MapPathSearch.TryBuildCoordinatePath(map, coords, out var coordinatePath, out var failure))
            {
                path = coordinatePath;
                GD.Print($"{RetryMod.LogPrefix}browser act {actIdx}: restored {path.Count} exact v3 coordinates");
            }
            else
            {
                GD.PrintErr($"{RetryMod.LogPrefix}browser act {actIdx}: v3 coordinate chain rejected: {failure}");
            }
        }

        path ??= MapPathSearch.FindPath(map, typeChain);
        if (path == null)
        {
            var closest = MapPathSearch.FindClosestPath(map, typeChain, exactHints, legacyHints);
            if (closest != null)
            {
                path = closest.Path;
                string mismatches = string.Join(", ", path.Select((point, floor) => (point, floor)).Where(x => x.floor < typeChain.Count && x.point.PointType != typeChain[x.floor]).Select(x => $"{x.floor}:{typeChain[x.floor]}→{x.point.PointType}"));
                GD.PrintErr($"{RetryMod.LogPrefix}browser act {actIdx}: exact history path unavailable; " + $"best legal path length={path.Count}/{entries.Count} " + $"typeMismatches={closest.TypeMismatches} exactCoordMatches={closest.ExactCoordinateMatches} " + $"legacyCoordMatches={closest.LegacyCoordinateMatches} complete={closest.Complete}" + (mismatches.Length > 0 ? $" [{mismatches}]" : ""));
            }
        }

        var visited = new List<MapCoord>();
        if (path != null)
        {
            if (!restoredExactMap)
            {
                int normalized = MapPathSearch.NormalizeHistoricalTypes(path, entries);
                if (normalized > 0)
                    GD.PrintErr($"{RetryMod.LogPrefix}browser fallback act {actIdx}: normalized {normalized} visited node types");
            }

            foreach (var n in path)
                visited.Add(n.coord);
        }

        return (map, visited);
    }

    internal void SetCurrentActIndex(int idx)
    {
        if (_session._runState == null)
            return;
        var prop = typeof(RunState).GetProperty(nameof(RunState.CurrentActIndex));
        prop?.SetValue(_session._runState, idx);
    }

    internal void ReplaceVisitedCoords(IEnumerable<MapCoord> coords)
    {
        if (_session._runState == null)
            return;
        var field = typeof(RunState).GetField("_visitedMapCoords", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(_session._runState)is List<MapCoord> list)
        {
            list.Clear();
            list.AddRange(coords);
        }
    }
}
