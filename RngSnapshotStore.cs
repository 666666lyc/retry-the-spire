// Sidecar JSON file capturing the RunRngSet.Counters at each map-
// point transition during play, plus the actual coord visited at
// each floor. Without per-floor coord storage we'd have to *guess*
// which (col,row) the original player visited at floor N — the
// MapPointHistoryEntry doesn't carry coord info.
//
// File: user://retry_the_spire_rng_snapshots.json
// Schema v2:
//   {
//     "version": 2,
//     "entries": {
//       "<seed>|<act>|<floor>": {
//         "row": <int>, "col": <int>,
//         "counters": { "<RunRngType>": <int>, ... }
//       },
//       ...
//     }
//   }
//
// We still read the v1 shape (top-level dict keyed by
// "<seed>|<act>|<row,col>" → counters dict) for backward compat,
// but it's lossless-degraded: no floor index means no exact-floor
// lookup is possible for old entries. Capture is performed by
// RngSnapshotCapture during real (non-retry) play; consumed here by
// RetryRunner.NavigateToTarget when retrying.
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

public static class RngSnapshotStore
{
    private const string FileName = "retry_the_spire_rng_snapshots.json";
    private const string LegacyFileName = "retry_rng_snapshots.json";
    private const int SchemaVersion = 2;

    public sealed class Entry
    {
        public int Row;
        public int Col;
        public Dictionary<RunRngType, int> Counters = new();
    }

    // Key: $"{seed}|{act}|{floor}"
    private static Dictionary<string, Entry>? _cache;

    public static void Capture(string seed, int actIndex, int floor, MapCoord coord, Dictionary<RunRngType, int> counters)
    {
        EnsureLoaded();
        if (_cache == null) return;
        _cache[KeyFor(seed, actIndex, floor)] = new Entry
        {
            Row = coord.row,
            Col = coord.col,
            Counters = new Dictionary<RunRngType, int>(counters),
        };
        TrySave();
    }

    // Try to find the exact coord the original player visited at
    // (seed, act, floor). Returns null if no snapshot exists.
    public static MapCoord? TryGetCoord(string seed, int actIndex, int floor)
    {
        EnsureLoaded();
        if (_cache == null) return null;
        if (!_cache.TryGetValue(KeyFor(seed, actIndex, floor), out var e)) return null;
        return new MapCoord { row = e.Row, col = e.Col };
    }

    // Fast-forward the LIVE RunRngSet to the counters captured at
    // the given (seed, act, floor). Returns true if a snapshot was
    // found and applied.
    public static bool TryApplyLive(MegaCrit.Sts2.Core.Runs.RunRngSet liveRng, string seed, int actIndex, int floor)
    {
        EnsureLoaded();
        if (_cache == null) return false;
        if (!_cache.TryGetValue(KeyFor(seed, actIndex, floor), out var e)) return false;
        var save = new SerializableRunRngSet { Seed = seed };
        foreach (var kv in e.Counters) save.Counters[kv.Key] = kv.Value;
        try { liveRng.LoadFromSerializable(save); }
        catch (System.Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot apply: {ex.Message}");
            return false;
        }
        return true;
    }

    public static bool HasSnapshot(string seed, int actIndex, int floor)
    {
        EnsureLoaded();
        return _cache != null && _cache.ContainsKey(KeyFor(seed, actIndex, floor));
    }

    private static string KeyFor(string seed, int actIndex, int floor) =>
        $"{seed}|{actIndex}|{floor}";

    private static string FilePath(string fileName = FileName)
    {
        return "user://" + fileName;
    }

    private static void EnsureLoaded()
    {
        if (_cache != null) return;
        _cache = new Dictionary<string, Entry>();
        try
        {
            bool importedLegacy = false;
            var f = FileAccess.Open(FilePath(), FileAccess.ModeFlags.Read);
            if (f == null)
            {
                // One-way, non-destructive compatibility import from the
                // upstream Retry filename. The legacy file remains intact;
                // subsequent writes go only to Retry the Spire's own file.
                f = FileAccess.Open(FilePath(LegacyFileName), FileAccess.ModeFlags.Read);
                importedLegacy = f != null;
            }
            if (f == null) return;
            string json;
            using (f) json = f.GetAsText();
            if (string.IsNullOrEmpty(json)) return;
            var parser = new Json();
            if (parser.Parse(json) != Error.Ok) return;
            if (parser.Data.AsGodotDictionary() is not Godot.Collections.Dictionary outer) return;

            if (outer.ContainsKey("version") && outer["version"].AsInt32() >= 2)
            {
                if (outer["entries"].AsGodotDictionary() is not Godot.Collections.Dictionary entries) return;
                foreach (var key in entries.Keys)
                {
                    if (entries[key].AsGodotDictionary() is not Godot.Collections.Dictionary e) continue;
                    var entry = new Entry
                    {
                        Row = e.ContainsKey("row") ? e["row"].AsInt32() : 0,
                        Col = e.ContainsKey("col") ? e["col"].AsInt32() : 0,
                    };
                    if (e.ContainsKey("counters") && e["counters"].AsGodotDictionary() is Godot.Collections.Dictionary ctrs)
                    {
                        foreach (var ck in ctrs.Keys)
                        {
                            if (System.Enum.TryParse<RunRngType>(ck.AsString(), out var t))
                                entry.Counters[t] = ctrs[ck].AsInt32();
                        }
                    }
                    _cache[key.AsString()] = entry;
                }
                if (importedLegacy)
                {
                    GD.Print($"{RetryMod.LogPrefix}imported legacy Retry RNG snapshots without modifying the original file");
                    TrySave();
                }
            }
            else
            {
                // Legacy v1 shape — top-level keyed by "<seed>|<act>|<row,col>"
                // No floor info recoverable. Skip; rely on fresh capture going forward.
                GD.Print($"{RetryMod.LogPrefix}snapshot store v1 detected — ignoring for floor-keyed lookups (rebuild by replaying)");
            }
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot load: {ex.Message}");
        }
    }

    private static void TrySave()
    {
        if (_cache == null) return;
        try
        {
            var entries = new Godot.Collections.Dictionary();
            foreach (var (k, e) in _cache)
            {
                var inner = new Godot.Collections.Dictionary
                {
                    ["row"] = e.Row,
                    ["col"] = e.Col,
                };
                var ctrs = new Godot.Collections.Dictionary();
                foreach (var (rt, c) in e.Counters) ctrs[rt.ToString()] = c;
                inner["counters"] = ctrs;
                entries[k] = inner;
            }
            var outer = new Godot.Collections.Dictionary
            {
                ["version"] = SchemaVersion,
                ["entries"] = entries,
            };
            var json = Json.Stringify(outer);
            using var f = FileAccess.Open(FilePath(), FileAccess.ModeFlags.Write);
            if (f == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}snapshot save: cannot open {FilePath()}");
                return;
            }
            f.StoreString(json);
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot save: {ex.Message}");
        }
    }
}
