// Sidecar JSON file capturing the RunRngSet.Counters at each map-
// point transition during play, plus the actual coord visited at
// each floor. Without per-floor coord storage we'd have to *guess*
// which (col,row) the original player visited at floor N — the
// MapPointHistoryEntry doesn't carry coord info.
//
// File: user://retry_the_spire_rng_snapshots.json
// Schema v3:
//   {
//     "version": 3,
//     "entries": {
//       "<startTime>|<seed>|<act>|<floor>": {
//         "row": <int>, "col": <int>,
//         "counters": { "<RunRngType>": <int>, ... }
//       },
//       ...
//     },
//     "legacy_entries": { "<seed>|<act>|<floor>": { ... } }
//   }
//
// v2 entries remain readable as low-confidence legacy hints. Their
// seed-only identity is ambiguous when the same seed is retried more
// than once, so v3 never treats them as authoritative coordinates.
// We still detect the v1 shape (top-level dict keyed by
// "<seed>|<act>|<row,col>" → counters dict) for backward compat,
// but it's lossless-degraded: no floor index means no exact-floor
// lookup is possible for old entries. Capture is performed by
// RngSnapshotCapture during real (non-retry) play; consumed here by
// RetryRunner.NavigateToTarget when retrying.
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

public static class RngSnapshotStore
{
    private const string FileName = "retry_the_spire_rng_snapshots.json";
    private const string LegacyFileName = "retry_rng_snapshots.json";
    private const int SchemaVersion = 3;
    private static readonly FieldInfo? RunStartTimeField = typeof(RunManager).GetField(
        "_startTime", BindingFlags.Instance | BindingFlags.NonPublic);

    public sealed class Entry
    {
        public int Row;
        public int Col;
        public Dictionary<RunRngType, int> Counters = new();
    }

    // v3 key: $"{startTime}|{seed}|{act}|{floor}"
    private static Dictionary<string, Entry>? _cache;
    // v2 key: $"{seed}|{act}|{floor}"
    private static Dictionary<string, Entry>? _legacyCache;

    public static void Capture(
        long startTime,
        string seed,
        int actIndex,
        int floor,
        MapCoord coord,
        Dictionary<RunRngType, int> counters)
    {
        EnsureLoaded();
        if (_cache == null || startTime <= 0) return;
        _cache[KeyFor(startTime, seed, actIndex, floor)] = new Entry
        {
            Row = coord.row,
            Col = coord.col,
            Counters = new Dictionary<RunRngType, int>(counters),
        };
        TrySave();
    }

    public static long GetCurrentRunStartTime()
        => RunStartTimeField?.GetValue(RunManager.Instance) is long value ? value : 0;

    public static void CaptureCoordinates(
        long startTime,
        string seed,
        int actIndex,
        IReadOnlyList<MapCoord> coords)
    {
        EnsureLoaded();
        if (_cache == null || startTime <= 0) return;
        for (int floor = 0; floor < coords.Count; floor++)
        {
            string key = KeyFor(startTime, seed, actIndex, floor);
            if (!_cache.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _cache[key] = entry;
            }
            entry.Row = coords[floor].row;
            entry.Col = coords[floor].col;
        }
        TrySave();
    }

    public static void CopyExactCoordinates(
        long sourceStartTime,
        long destinationStartTime,
        string seed,
        int actIndex,
        int floorCount)
    {
        EnsureLoaded();
        if (_cache == null || sourceStartTime <= 0 || destinationStartTime <= 0) return;
        bool changed = false;
        for (int floor = 0; floor < floorCount; floor++)
        {
            if (!_cache.TryGetValue(KeyFor(sourceStartTime, seed, actIndex, floor), out var source))
                continue;
            string destinationKey = KeyFor(destinationStartTime, seed, actIndex, floor);
            _cache[destinationKey] = new Entry
            {
                Row = source.Row,
                Col = source.Col,
                Counters = new Dictionary<RunRngType, int>(source.Counters),
            };
            changed = true;
        }
        if (changed) TrySave();
    }

    // Try to find the exact coord the original player visited at
    // (seed, act, floor). Returns null if no snapshot exists.
    public static MapCoord? TryGetExactCoord(
        long startTime, string seed, int actIndex, int floor)
    {
        EnsureLoaded();
        if (_cache == null || startTime <= 0) return null;
        if (!_cache.TryGetValue(KeyFor(startTime, seed, actIndex, floor), out var e)) return null;
        return new MapCoord { row = e.Row, col = e.Col };
    }

    public static MapCoord? TryGetLegacyCoord(string seed, int actIndex, int floor)
    {
        EnsureLoaded();
        if (_legacyCache == null) return null;
        if (!_legacyCache.TryGetValue(LegacyKeyFor(seed, actIndex, floor), out var e)) return null;
        return new MapCoord { row = e.Row, col = e.Col };
    }

    public static MapCoord? TryGetCoord(
        long startTime, string seed, int actIndex, int floor, bool allowLegacy = true)
        => TryGetExactCoord(startTime, seed, actIndex, floor)
            ?? (allowLegacy ? TryGetLegacyCoord(seed, actIndex, floor) : null);

    // Fast-forward the LIVE RunRngSet to the counters captured at
    // the given (seed, act, floor). Returns true if a snapshot was
    // found and applied.
    public static bool TryApplyLive(
        MegaCrit.Sts2.Core.Runs.RunRngSet liveRng,
        long startTime,
        string seed,
        int actIndex,
        int floor)
    {
        EnsureLoaded();
        Entry? e = null;
        if (_cache != null && startTime > 0)
            _cache.TryGetValue(KeyFor(startTime, seed, actIndex, floor), out e);
        if (e == null && _legacyCache != null)
            _legacyCache.TryGetValue(LegacyKeyFor(seed, actIndex, floor), out e);
        if (e == null) return false;
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

    public static bool HasSnapshot(long startTime, string seed, int actIndex, int floor)
    {
        EnsureLoaded();
        return (_cache != null && startTime > 0
                && _cache.ContainsKey(KeyFor(startTime, seed, actIndex, floor)))
            || (_legacyCache != null
                && _legacyCache.ContainsKey(LegacyKeyFor(seed, actIndex, floor)));
    }

    private static string KeyFor(long startTime, string seed, int actIndex, int floor) =>
        $"{startTime}|{seed}|{actIndex}|{floor}";

    private static string LegacyKeyFor(string seed, int actIndex, int floor) =>
        $"{seed}|{actIndex}|{floor}";

    private static string FilePath(string fileName = FileName)
    {
        return "user://" + fileName;
    }

    private static void EnsureLoaded()
    {
        if (_cache != null) return;
        _cache = new Dictionary<string, Entry>();
        _legacyCache = new Dictionary<string, Entry>();
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

            int version = outer.ContainsKey("version") ? outer["version"].AsInt32() : 1;
            if (version >= 3)
            {
                if (outer.ContainsKey("entries")
                    && outer["entries"].AsGodotDictionary() is Godot.Collections.Dictionary entries)
                    ReadEntries(entries, _cache);
                if (outer.ContainsKey("legacy_entries")
                    && outer["legacy_entries"].AsGodotDictionary() is Godot.Collections.Dictionary legacyEntries)
                    ReadEntries(legacyEntries, _legacyCache);
                if (importedLegacy)
                {
                    GD.Print($"{RetryMod.LogPrefix}imported Retry snapshot sidecar without modifying the original file");
                    TrySave();
                }
            }
            else if (version == 2)
            {
                if (outer.ContainsKey("entries")
                    && outer["entries"].AsGodotDictionary() is Godot.Collections.Dictionary entries)
                    ReadEntries(entries, _legacyCache);
                GD.Print($"{RetryMod.LogPrefix}snapshot store v2 loaded as legacy coordinate hints");
                if (importedLegacy) TrySave();
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

    private static void ReadEntries(
        Godot.Collections.Dictionary entries,
        Dictionary<string, Entry> destination)
    {
        foreach (var key in entries.Keys)
        {
            if (entries[key].AsGodotDictionary() is not Godot.Collections.Dictionary e) continue;
            var entry = new Entry
            {
                Row = e.ContainsKey("row") ? e["row"].AsInt32() : 0,
                Col = e.ContainsKey("col") ? e["col"].AsInt32() : 0,
            };
            if (e.ContainsKey("counters")
                && e["counters"].AsGodotDictionary() is Godot.Collections.Dictionary ctrs)
            {
                foreach (var ck in ctrs.Keys)
                {
                    if (System.Enum.TryParse<RunRngType>(ck.AsString(), out var t))
                        entry.Counters[t] = ctrs[ck].AsInt32();
                }
            }
            destination[key.AsString()] = entry;
        }
    }

    private static void TrySave()
    {
        if (_cache == null || _legacyCache == null) return;
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
            var legacyEntries = new Godot.Collections.Dictionary();
            foreach (var (k, e) in _legacyCache)
            {
                var inner = new Godot.Collections.Dictionary
                {
                    ["row"] = e.Row,
                    ["col"] = e.Col,
                };
                var ctrs = new Godot.Collections.Dictionary();
                foreach (var (rt, c) in e.Counters) ctrs[rt.ToString()] = c;
                inner["counters"] = ctrs;
                legacyEntries[k] = inner;
            }
            var outer = new Godot.Collections.Dictionary
            {
                ["version"] = SchemaVersion,
                ["entries"] = entries,
                ["legacy_entries"] = legacyEntries,
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
