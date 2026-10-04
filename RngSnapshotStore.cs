// Versioned, Steam-cloud-backed snapshots captured at room entry.
//
// The store lives beside the game's profile-scoped save files and is written
// through the game's own ISaveStore. It never modifies native .run history.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

public static class RngSnapshotStore
{
    private const string FileName = "retry_the_spire_snapshots.save";
    private const string OldTopLevelFileName = "retry_the_spire_rng_snapshots.json";
    private const string Header = "RTS-SNAPSHOTS-4\n";
    private const int SchemaVersion = 4;
    private const int MaxEncodedBytes = 4 * 1024 * 1024;

    private static readonly FieldInfo? RunStartTimeField = typeof(RunManager).GetField(
        "_startTime", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? SaveStoreField = typeof(SaveManager).GetField(
        "_saveStore", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly JsonSerializerOptions StoreJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed class Entry
    {
        public int Row { get; set; }
        public int Col { get; set; }
        public Dictionary<RunRngType, int> Counters { get; set; } = new();
        public string? PlayersJson { get; set; }
        public long CapturedAt { get; set; }
    }

    private sealed class StoreData
    {
        public int Version { get; set; } = SchemaVersion;
        public Dictionary<string, Entry> Entries { get; set; } = new();
    }

    private static Dictionary<string, Entry>? _cache;
    private static string? _cachePath;
    private static bool _loadedFromBackup;

    public static void Capture(
        long startTime,
        string seed,
        int actIndex,
        int floor,
        MapCoord coord,
        Dictionary<RunRngType, int> counters,
        IReadOnlyList<SerializablePlayer>? players = null)
    {
        EnsureLoaded();
        if (_cache == null || startTime <= 0) return;

        string key = KeyFor(startTime, seed, actIndex, floor);
        if (!_cache.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            _cache[key] = entry;
        }
        entry.Row = coord.row;
        entry.Col = coord.col;
        entry.Counters = new Dictionary<RunRngType, int>(counters);
        entry.CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (players != null)
        {
            try
            {
                entry.PlayersJson = JsonSerializer.Serialize(
                    players.ToList(), JsonSerializationUtility.Options);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}snapshot players serialize: {ex.Message}");
            }
        }
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
                entry = new Entry { CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                _cache[key] = entry;
            }
            entry.Row = coords[floor].row;
            entry.Col = coords[floor].col;
        }
        TrySave();
    }

    // Kept under its old name for the existing launch callers. Version 4
    // copies the complete entry, not merely its coordinate.
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
            _cache[KeyFor(destinationStartTime, seed, actIndex, floor)] = CloneEntry(source);
            changed = true;
        }
        if (changed) TrySave();
    }

    public static MapCoord? TryGetExactCoord(
        long startTime, string seed, int actIndex, int floor)
    {
        EnsureLoaded();
        if (_cache == null || startTime <= 0) return null;
        if (!_cache.TryGetValue(KeyFor(startTime, seed, actIndex, floor), out var entry)) return null;
        return new MapCoord { row = entry.Row, col = entry.Col };
    }

    // Version 4 intentionally drops ambiguous seed-only legacy hints.
    public static MapCoord? TryGetLegacyCoord(string seed, int actIndex, int floor) => null;

    public static MapCoord? TryGetCoord(
        long startTime, string seed, int actIndex, int floor, bool allowLegacy = true)
        => TryGetExactCoord(startTime, seed, actIndex, floor);

    public static SerializablePlayer? TryGetPlayerSnapshot(
        long startTime,
        string seed,
        int actIndex,
        int floor,
        ulong playerId)
    {
        EnsureLoaded();
        if (_cache == null || startTime <= 0) return null;
        if (!_cache.TryGetValue(KeyFor(startTime, seed, actIndex, floor), out var entry)
            || string.IsNullOrEmpty(entry.PlayersJson))
            return null;
        try
        {
            var players = JsonSerializer.Deserialize<List<SerializablePlayer>>(
                entry.PlayersJson, JsonSerializationUtility.Options);
            return players?.FirstOrDefault(p => p.NetId == playerId);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot player deserialize: {ex.Message}");
            return null;
        }
    }

    public static bool TryApplyLive(
        RunRngSet liveRng,
        long startTime,
        string seed,
        int actIndex,
        int floor)
    {
        EnsureLoaded();
        if (_cache == null
            || !_cache.TryGetValue(KeyFor(startTime, seed, actIndex, floor), out var entry))
            return false;
        var save = new SerializableRunRngSet { Seed = seed };
        foreach (var kv in entry.Counters) save.Counters[kv.Key] = kv.Value;
        try { liveRng.LoadFromSerializable(save); }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot apply: {ex.Message}");
            return false;
        }
        return true;
    }

    public static bool HasSnapshot(long startTime, string seed, int actIndex, int floor)
    {
        EnsureLoaded();
        return _cache != null && startTime > 0
            && _cache.ContainsKey(KeyFor(startTime, seed, actIndex, floor));
    }

    private static Entry CloneEntry(Entry source) => new()
    {
        Row = source.Row,
        Col = source.Col,
        Counters = new Dictionary<RunRngType, int>(source.Counters),
        PlayersJson = source.PlayersJson,
        CapturedAt = source.CapturedAt,
    };

    private static string KeyFor(long startTime, string seed, int actIndex, int floor) =>
        $"{startTime}|{seed}|{actIndex}|{floor}";

    private static bool TryGetCloudStore(out ISaveStore store, out string path)
    {
        store = null!;
        path = "";
        try
        {
            var manager = SaveManager.Instance;
            if (manager == null || !manager.IsProfileInitialized) return false;
            if (SaveStoreField?.GetValue(manager) is not ISaveStore found) return false;
            store = found;
            path = manager.GetProfileScopedPath("saves/" + FileName);
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot cloud store: {ex.Message}");
            return false;
        }
    }

    private static void EnsureLoaded()
    {
        if (!TryGetCloudStore(out var store, out var path))
        {
            _cache ??= new Dictionary<string, Entry>();
            return;
        }
        if (_cache != null && string.Equals(_cachePath, path, StringComparison.Ordinal)) return;

        // Profiles have independent Steam-cloud namespaces. Reload instead of
        // letting a static cache from profile N bleed into profile N+1. This
        // also retries after an early main-menu call before profile init.
        _cachePath = path;
        _cache = new Dictionary<string, Entry>();
        _loadedFromBackup = false;
        try
        {
            StoreData? data = null;
            Exception? primaryFailure = null;
            if (store.FileExists(path))
            {
                try { data = Decode(ReadRequired(store, path)); }
                catch (Exception ex) { primaryFailure = ex; }
            }

            string backup = path + ".backup";
            if (data == null && store.FileExists(backup))
            {
                data = Decode(ReadRequired(store, backup));
                _loadedFromBackup = true;
                GD.PrintErr($"{RetryMod.LogPrefix}snapshot primary unavailable; recovered cloud backup: {primaryFailure?.Message ?? "primary missing"}");
            }
            if (data == null)
            {
                if (primaryFailure != null) throw primaryFailure;
                return;
            }
            if (data.Version != SchemaVersion)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}snapshot schema {data.Version} unsupported; starting empty v{SchemaVersion} store");
                return;
            }
            _cache = data.Entries ?? new Dictionary<string, Entry>();
            GD.Print($"{RetryMod.LogPrefix}snapshot cloud store loaded path={path} entries={_cache.Count}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot load: {ex.Message}; using reconstructed history state");
            _cache = new Dictionary<string, Entry>();
        }
    }

    private static void TrySave()
    {
        if (_cache == null || !TryGetCloudStore(out var store, out var path)) return;
        // Temporary/backup files are deliberately local-only. Sending their
        // renames through CloudSaveStore would create extra Steam Cloud files;
        // only the final profile-scoped .save belongs in the cloud cache.
        ISaveStore localStore = store is CloudSaveStore cloud ? cloud.LocalStore : store;
        // The game's cloud directory scan ignores names ending in .backup,
        // so a crash cannot leave a temporary file eligible for later sync.
        string temp = path + ".tmp.backup";
        string backup = path + ".backup";
        try
        {
            string encoded = Encode(new StoreData { Entries = _cache });
            if (Encoding.UTF8.GetByteCount(encoded) > MaxEncodedBytes)
            {
                PruneToLimit();
                encoded = Encode(new StoreData { Entries = _cache });
            }
            if (Encoding.UTF8.GetByteCount(encoded) > MaxEncodedBytes)
                throw new InvalidOperationException("snapshot store still exceeds 4 MiB after pruning");

            if (localStore.FileExists(temp)) localStore.DeleteFile(temp);
            localStore.WriteFile(temp, encoded);
            if (!localStore.FileExists(temp))
                throw new IOException("temporary snapshot file was not created");
            _ = Decode(ReadRequired(localStore, temp));

            if (_loadedFromBackup)
            {
                // Keep the known-good backup until the replacement primary is
                // safely installed; the existing primary may be corrupt.
                if (localStore.FileExists(path)) localStore.DeleteFile(path);
            }
            else
            {
                if (localStore.FileExists(backup)) localStore.DeleteFile(backup);
                if (localStore.FileExists(path)) localStore.RenameFile(path, backup);
            }
            try { localStore.RenameFile(temp, path); }
            catch
            {
                if (localStore.FileExists(backup) && !localStore.FileExists(path))
                    localStore.RenameFile(backup, path);
                throw;
            }

            if (store is CloudSaveStore cloudStore)
            {
                // Use the remote store directly so a Steam write failure is
                // observable instead of being swallowed by CloudSaveStore's
                // "local file preserved" fallback.
                cloudStore.CloudStore.WriteFile(path, encoded);
                localStore.SetLastModifiedTime(
                    path, cloudStore.CloudStore.GetLastModifiedTime(path));
                GD.Print($"{RetryMod.LogPrefix}snapshot cloud write path={path} bytes={Encoding.UTF8.GetByteCount(encoded)}");
            }

            _loadedFromBackup = false;
            DeleteOldTopLevelStore();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot save: {ex.Message}");
            try { if (localStore.FileExists(temp)) localStore.DeleteFile(temp); } catch { }
        }
    }

    private static void PruneToLimit()
    {
        if (_cache == null || _cache.Count == 0) return;
        long currentStart = GetCurrentRunStartTime();
        var historyStarts = new HashSet<long>();
        try
        {
            foreach (string name in SaveManager.Instance.GetAllRunHistoryNames())
            {
                if (long.TryParse(Path.GetFileNameWithoutExtension(name), out long value))
                    historyStarts.Add(value);
            }
        }
        catch { }

        var groups = _cache
            .GroupBy(kv => ParseStartTime(kv.Key))
            .Select(g => new
            {
                Start = g.Key,
                Keys = g.Select(kv => kv.Key).ToList(),
                Captured = g.Max(kv => kv.Value.CapturedAt),
                IsLive = g.Key == currentStart,
                HasHistory = historyStarts.Contains(g.Key),
            })
            .OrderBy(g => g.IsLive)
            .ThenBy(g => g.HasHistory)
            .ThenBy(g => g.Captured)
            .ToList();

        foreach (var group in groups)
        {
            if (Encoding.UTF8.GetByteCount(Encode(new StoreData { Entries = _cache })) <= MaxEncodedBytes)
                break;
            if (group.IsLive) continue;
            foreach (string key in group.Keys) _cache.Remove(key);
            GD.Print($"{RetryMod.LogPrefix}snapshot prune startTime={group.Start} entries={group.Keys.Count}");
        }

        // A single unusually large history can exceed the cap by itself.
        // Continue at room granularity, preferring non-current and oldest
        // entries. Any removed room still has the normal reconstruction path.
        foreach (var entry in _cache
                     .OrderBy(kv => ParseStartTime(kv.Key) == currentStart)
                     .ThenBy(kv => kv.Value.CapturedAt)
                     .ToList())
        {
            if (Encoding.UTF8.GetByteCount(Encode(new StoreData { Entries = _cache })) <= MaxEncodedBytes)
                break;
            _cache.Remove(entry.Key);
            GD.Print($"{RetryMod.LogPrefix}snapshot prune entry={entry.Key}");
        }
    }

    private static long ParseStartTime(string key)
    {
        int separator = key.IndexOf('|');
        return separator > 0 && long.TryParse(key[..separator], out long value) ? value : 0;
    }

    private static string Encode(StoreData data)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(data, StoreJsonOptions);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(json, 0, json.Length);
        return Header + Convert.ToBase64String(output.ToArray());
    }

    private static StoreData Decode(string encoded)
    {
        if (!encoded.StartsWith(Header, StringComparison.Ordinal))
            throw new InvalidDataException("snapshot header is missing");
        byte[] compressed = Convert.FromBase64String(encoded[Header.Length..]);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<StoreData>(gzip, StoreJsonOptions)
            ?? throw new InvalidDataException("snapshot JSON was empty");
    }

    private static string ReadRequired(ISaveStore store, string path) =>
        store.ReadFile(path) ?? throw new IOException($"snapshot file {path} could not be read");

    private static void DeleteOldTopLevelStore()
    {
        try
        {
            string oldPath = ProjectSettings.GlobalizePath("user://" + OldTopLevelFileName);
            if (!File.Exists(oldPath)) return;
            File.Delete(oldPath);
            GD.Print($"{RetryMod.LogPrefix}removed obsolete top-level RNG snapshot store");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}remove obsolete RNG snapshot store: {ex.Message}");
        }
    }
}
