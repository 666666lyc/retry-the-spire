// Compact, versioned room-entry snapshots stored through the game's ISaveStore.
// Maps and players use native packet serializers, are deduplicated, and the
// complete binary payload is gzip-compressed before its text-safe wrapper.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

public static class RngSnapshotStore
{
    private const string FileName = "retry_the_spire_snapshots.save";
    private const string OldTopLevelFileName = "retry_the_spire_rng_snapshots.json";
    private const string Header = "RTS-SNAPSHOTS-5\n";
    private const string LegacyHeader = "RTS-SNAPSHOTS-4\n";
    private const int SchemaVersion = 5;
    private const int SoftEncodedBytes = 2 * 1024 * 1024;
    private const int MaxEncodedBytes = 4 * 1024 * 1024;
    private const int MaxBlobBytes = 8 * 1024 * 1024;
    private const int MaxCollectionCount = 1_000_000;
    private const uint PayloadMagic = 0x35535452; // RTS5

    private static readonly FieldInfo? RunStartTimeField = typeof(RunManager).GetField(
        "_startTime", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? SaveStoreField = typeof(SaveManager).GetField(
        "_saveStore", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class StoreData
    {
        public List<byte[]> Maps { get; } = new();
        public List<byte[]> Players { get; } = new();
        public List<RunRecord> Runs { get; } = new();
    }

    private sealed class RunRecord
    {
        public long StartTime;
        public string Seed = "";
        public long CapturedAt;
        public List<ActRecord> Acts { get; } = new();
    }

    private sealed class ActRecord
    {
        public int Index;
        public int MapBlobId = -1;
        public List<RoomRecord> Rooms { get; } = new();
    }

    private sealed class RoomRecord
    {
        public int Floor;
        public int Row;
        public int Col;
        public Dictionary<RunRngType, int> Counters { get; set; } = new();
        public List<int> PlayerBlobIds { get; set; } = new();
    }

    private sealed class LegacyEntry
    {
        public int Row { get; set; }
        public int Col { get; set; }
        public Dictionary<RunRngType, int> Counters { get; set; } = new();
        public string? PlayersJson { get; set; }
        public long CapturedAt { get; set; }
    }

    private sealed class LegacyStoreData
    {
        public int Version { get; set; }
        public Dictionary<string, LegacyEntry> Entries { get; set; } = new();
    }

    private static StoreData? _cache;
    private static string? _cachePath;
    private static bool _loadedFromBackup;
    private static byte[]? _lastGoodPayload;
    private static Dictionary<string, int> _mapHashes = new(StringComparer.Ordinal);
    private static Dictionary<string, int> _playerHashes = new(StringComparer.Ordinal);

    public static void Capture(
        long startTime,
        string seed,
        int actIndex,
        int floor,
        MapCoord coord,
        Dictionary<RunRngType, int> counters,
        IReadOnlyList<SerializablePlayer>? players = null,
        ActMap? map = null)
    {
        EnsureLoaded();
        if (_cache == null || startTime <= 0) return;

        var run = GetOrCreateRun(startTime, seed);
        var act = GetOrCreateAct(run, actIndex);
        var room = GetOrCreateRoom(act, floor);
        room.Row = coord.row;
        room.Col = coord.col;
        room.Counters = new Dictionary<RunRngType, int>(counters);
        run.CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (players != null)
        {
            room.PlayerBlobIds = new List<int>(players.Count);
            foreach (var player in players)
            {
                try { room.PlayerBlobIds.Add(AddPlayerBlob(SerializeNative(player))); }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}snapshot player serialize: {ex.Message}");
                }
            }
        }

        map ??= TryGetLiveMap(actIndex);
        if (map != null)
        {
            try { act.MapBlobId = AddMapBlob(SerializeNative(SerializableActMap.FromActMap(map))); }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}snapshot map serialize: {ex.Message}");
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
        var run = GetOrCreateRun(startTime, seed);
        var act = GetOrCreateAct(run, actIndex);
        for (int floor = 0; floor < coords.Count; floor++)
        {
            var room = GetOrCreateRoom(act, floor);
            room.Row = coords[floor].row;
            room.Col = coords[floor].col;
        }
        run.CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        TrySave();
    }

    // The old name is retained because callers also need RNG/player/map refs.
    public static void CopyExactCoordinates(
        long sourceStartTime,
        long destinationStartTime,
        string seed,
        int actIndex,
        int floorCount)
    {
        EnsureLoaded();
        if (_cache == null || sourceStartTime <= 0 || destinationStartTime <= 0) return;
        var sourceAct = FindRun(sourceStartTime, seed)?.Acts.FirstOrDefault(a => a.Index == actIndex);
        if (sourceAct == null) return;

        var destination = GetOrCreateRun(destinationStartTime, seed);
        var destinationAct = GetOrCreateAct(destination, actIndex);
        if (sourceAct.MapBlobId >= 0) destinationAct.MapBlobId = sourceAct.MapBlobId;
        bool changed = false;
        foreach (var source in sourceAct.Rooms.Where(r => r.Floor < floorCount))
        {
            var room = GetOrCreateRoom(destinationAct, source.Floor);
            room.Row = source.Row;
            room.Col = source.Col;
            room.Counters = new Dictionary<RunRngType, int>(source.Counters);
            room.PlayerBlobIds = new List<int>(source.PlayerBlobIds);
            changed = true;
        }
        if (changed || sourceAct.MapBlobId >= 0)
        {
            destination.CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            TrySave();
        }
    }

    public static MapCoord? TryGetExactCoord(long startTime, string seed, int actIndex, int floor)
    {
        EnsureLoaded();
        var room = FindRoom(startTime, seed, actIndex, floor);
        return room == null ? null : new MapCoord { row = room.Row, col = room.Col };
    }

    public static MapCoord? TryGetLegacyCoord(string seed, int actIndex, int floor) => null;

    public static MapCoord? TryGetCoord(
        long startTime, string seed, int actIndex, int floor, bool allowLegacy = true)
        => TryGetExactCoord(startTime, seed, actIndex, floor);

    public static SerializablePlayer? TryGetPlayerSnapshot(
        long startTime, string seed, int actIndex, int floor, ulong playerId)
    {
        EnsureLoaded();
        if (_cache == null) return null;
        var room = FindRoom(startTime, seed, actIndex, floor);
        if (room == null) return null;
        foreach (int id in room.PlayerBlobIds)
        {
            if (id < 0 || id >= _cache.Players.Count) continue;
            try
            {
                var player = DeserializePlayer(_cache.Players[id]);
                if (player.NetId == playerId) return player;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}snapshot player deserialize: {ex.Message}");
            }
        }
        return null;
    }

    public static bool TryGetActMap(
        long startTime, string seed, int actIndex, out SerializableActMap? map)
    {
        EnsureLoaded();
        map = null;
        if (_cache == null) return false;
        var act = FindRun(startTime, seed)?.Acts.FirstOrDefault(a => a.Index == actIndex);
        if (act == null || act.MapBlobId < 0 || act.MapBlobId >= _cache.Maps.Count) return false;
        try
        {
            map = DeserializeMap(_cache.Maps[act.MapBlobId]);
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot map deserialize act={actIndex}: {ex.Message}");
            return false;
        }
    }

    public static ActMap? TryCreateSavedActMap(long startTime, string seed, int actIndex)
        => TryGetActMap(startTime, seed, actIndex, out var map) && map != null
            ? new SavedActMap(map)
            : null;

    public static bool QueueSavedMapForGeneration(long startTime, string seed, int actIndex)
    {
        if (!TryGetActMap(startTime, seed, actIndex, out var map) || map == null) return false;
        var queued = RunManager.Instance.SavedMapsToLoad ?? new Dictionary<int, SerializableActMap>();
        queued[actIndex] = map;
        RunManager.Instance.SavedMapsToLoad = queued;
        GD.Print($"{RetryMod.LogPrefix}queued exact saved map act={actIndex}");
        return true;
    }

    public static bool TryApplyLive(
        RunRngSet liveRng, long startTime, string seed, int actIndex, int floor)
    {
        EnsureLoaded();
        var room = FindRoom(startTime, seed, actIndex, floor);
        if (room == null) return false;
        var save = new SerializableRunRngSet { Seed = seed };
        foreach (var kv in room.Counters) save.Counters[kv.Key] = kv.Value;
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
        return FindRoom(startTime, seed, actIndex, floor) != null;
    }

    public static bool HasMapSnapshot(long startTime, string seed, int actIndex)
    {
        EnsureLoaded();
        var act = FindRun(startTime, seed)?.Acts.FirstOrDefault(a => a.Index == actIndex);
        return _cache != null && act?.MapBlobId >= 0 && act.MapBlobId < _cache.Maps.Count;
    }

    public static string Fingerprint(ActMap map)
        => Convert.ToHexString(SHA256.HashData(
            SerializeNative(SerializableActMap.FromActMap(map))));

    public static string Fingerprint(SerializablePlayer player)
        => Convert.ToHexString(SHA256.HashData(SerializeNative(player)));

    public static int GetEncodedSizeForDiagnostics()
    {
        EnsureLoaded();
        return _cache == null ? 0 : EncodedSize(_cache);
    }

    private static ActMap? TryGetLiveMap(int actIndex)
    {
        try
        {
            var manager = RunManager.Instance;
            var state = manager == null ? null : StateAccessor.GetState(manager);
            return state != null && state.CurrentActIndex == actIndex ? state.Map : null;
        }
        catch { return null; }
    }

    private static RunRecord? FindRun(long startTime, string seed)
        => _cache?.Runs.FirstOrDefault(r => r.StartTime == startTime && r.Seed == seed);

    private static RoomRecord? FindRoom(long startTime, string seed, int actIndex, int floor)
        => FindRun(startTime, seed)?.Acts.FirstOrDefault(a => a.Index == actIndex)?
            .Rooms.FirstOrDefault(r => r.Floor == floor);

    private static RunRecord GetOrCreateRun(long startTime, string seed)
    {
        var existing = FindRun(startTime, seed);
        if (existing != null) return existing;
        var run = new RunRecord { StartTime = startTime, Seed = seed };
        _cache!.Runs.Add(run);
        return run;
    }

    private static ActRecord GetOrCreateAct(RunRecord run, int index)
    {
        var existing = run.Acts.FirstOrDefault(a => a.Index == index);
        if (existing != null) return existing;
        var act = new ActRecord { Index = index };
        run.Acts.Add(act);
        return act;
    }

    private static RoomRecord GetOrCreateRoom(ActRecord act, int floor)
    {
        var existing = act.Rooms.FirstOrDefault(r => r.Floor == floor);
        if (existing != null) return existing;
        var room = new RoomRecord { Floor = floor };
        act.Rooms.Add(room);
        return room;
    }

    private static int AddMapBlob(byte[] blob) => AddBlob(_cache!.Maps, _mapHashes, blob);
    private static int AddPlayerBlob(byte[] blob) => AddBlob(_cache!.Players, _playerHashes, blob);

    private static int AddBlob(List<byte[]> blobs, Dictionary<string, int> hashes, byte[] blob)
    {
        string hash = Convert.ToHexString(SHA256.HashData(blob));
        if (hashes.TryGetValue(hash, out int id)
            && id >= 0 && id < blobs.Count && blobs[id].AsSpan().SequenceEqual(blob))
            return id;
        id = blobs.Count;
        blobs.Add(blob);
        hashes[hash] = id;
        return id;
    }

    private static byte[] SerializeNative(SerializableActMap map)
    {
        var writer = new PacketWriter();
        map.Serialize(writer);
        return CopyWrittenBytes(writer);
    }

    private static byte[] SerializeNative(SerializablePlayer player)
    {
        var writer = new PacketWriter();
        player.Serialize(writer);
        return CopyWrittenBytes(writer);
    }

    private static byte[] CopyWrittenBytes(PacketWriter writer)
    {
        writer.ZeroByteRemainder();
        var result = new byte[writer.BytePosition];
        Buffer.BlockCopy(writer.Buffer, 0, result, 0, result.Length);
        return result;
    }

    private static SerializableActMap DeserializeMap(byte[] blob)
    {
        var result = new SerializableActMap();
        var reader = new PacketReader();
        reader.Reset(blob);
        result.Deserialize(reader);
        return result;
    }

    private static SerializablePlayer DeserializePlayer(byte[] blob)
    {
        var result = new SerializablePlayer();
        var reader = new PacketReader();
        reader.Reset(blob);
        result.Deserialize(reader);
        return result;
    }

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
            if (_cache == null) { _cache = new StoreData(); RebuildHashes(); }
            return;
        }
        if (_cache != null && string.Equals(_cachePath, path, StringComparison.Ordinal)) return;

        _cachePath = path;
        _cache = new StoreData();
        _loadedFromBackup = false;
        bool migrated = false;
        try
        {
            StoreData? data = null;
            byte[]? payload = null;
            Exception? primaryFailure = null;
            if (store.FileExists(path))
            {
                try { data = Decode(ReadRequired(store, path), out payload, out migrated); }
                catch (Exception ex) { primaryFailure = ex; }
            }
            string backup = path + ".backup";
            if (data == null && store.FileExists(backup))
            {
                data = Decode(ReadRequired(store, backup), out payload, out migrated);
                _loadedFromBackup = true;
                GD.PrintErr($"{RetryMod.LogPrefix}snapshot primary unavailable; recovered cloud backup: {primaryFailure?.Message ?? "primary missing"}");
            }
            if (data == null)
            {
                if (primaryFailure != null) throw primaryFailure;
                RebuildHashes();
                return;
            }
            _cache = data;
            RebuildHashes();
            _lastGoodPayload = payload ?? SerializePayload(_cache);
            int rooms = _cache.Runs.Sum(r => r.Acts.Sum(a => a.Rooms.Count));
            GD.Print($"{RetryMod.LogPrefix}snapshot cloud store loaded path={path} runs={_cache.Runs.Count} rooms={rooms} maps={_cache.Maps.Count}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot load: {ex.Message}; using reconstructed history state");
            _cache = new StoreData();
            RebuildHashes();
        }
        if (migrated)
        {
            GD.Print($"{RetryMod.LogPrefix}snapshot schema v4 migrated to compact v{SchemaVersion}");
            TrySave();
        }
    }

    private static void TrySave()
    {
        if (_cache == null || !TryGetCloudStore(out var store, out var path)) return;
        ISaveStore localStore = store is CloudSaveStore cloud ? cloud.LocalStore : store;
        string temp = path + ".tmp.backup";
        string backup = path + ".backup";
        try
        {
            string encoded = Encode(_cache, out var payload);
            if (Encoding.UTF8.GetByteCount(encoded) > SoftEncodedBytes)
            {
                PruneToLimit(SoftEncodedBytes);
                encoded = Encode(_cache, out payload);
            }
            int encodedBytes = Encoding.UTF8.GetByteCount(encoded);
            if (encodedBytes > MaxEncodedBytes)
            {
                if (_lastGoodPayload != null)
                {
                    _cache = DeserializePayload(_lastGoodPayload);
                    RebuildHashes();
                }
                GD.PrintErr($"{RetryMod.LogPrefix}snapshot current run exceeds 4 MiB; newest capture was not persisted");
                return;
            }

            if (localStore.FileExists(temp)) localStore.DeleteFile(temp);
            localStore.WriteFile(temp, encoded);
            if (!localStore.FileExists(temp)) throw new IOException("temporary snapshot file was not created");
            _ = Decode(ReadRequired(localStore, temp), out _, out _);

            if (_loadedFromBackup)
            {
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
                cloudStore.CloudStore.WriteFile(path, encoded);
                localStore.SetLastModifiedTime(path, cloudStore.CloudStore.GetLastModifiedTime(path));
            }
            _lastGoodPayload = payload;
            _loadedFromBackup = false;
            DeleteOldTopLevelStore();
            GD.Print($"{RetryMod.LogPrefix}snapshot write path={path} encodedBytes={encodedBytes} payloadBytes={payload.Length} runs={_cache.Runs.Count}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot save: {ex.Message}");
            try { if (localStore.FileExists(temp)) localStore.DeleteFile(temp); } catch { }
        }
    }

    private static void PruneToLimit(int targetBytes)
    {
        if (_cache == null || _cache.Runs.Count == 0) return;
        long currentStart = GetCurrentRunStartTime();
        var historyStarts = new HashSet<long>();
        try
        {
            foreach (string name in SaveManager.Instance.GetAllRunHistoryNames())
                if (long.TryParse(Path.GetFileNameWithoutExtension(name), out long value)) historyStarts.Add(value);
        }
        catch { }

        var candidates = _cache.Runs.Where(r => r.StartTime != currentStart)
            .OrderBy(r => historyStarts.Contains(r.StartTime)).ThenBy(r => r.CapturedAt).ToList();
        foreach (var run in candidates)
        {
            if (EncodedSize(_cache) <= targetBytes) break;
            _cache.Runs.Remove(run);
            GD.Print($"{RetryMod.LogPrefix}snapshot prune run startTime={run.StartTime} hasHistory={historyStarts.Contains(run.StartTime)}");
        }
        GarbageCollectBlobs();
    }

    private static void GarbageCollectBlobs()
    {
        if (_cache == null) return;
        var usedMaps = _cache.Runs.SelectMany(r => r.Acts).Where(a => a.MapBlobId >= 0)
            .Select(a => a.MapBlobId).Distinct().OrderBy(i => i).ToList();
        var usedPlayers = _cache.Runs.SelectMany(r => r.Acts).SelectMany(a => a.Rooms)
            .SelectMany(r => r.PlayerBlobIds).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        var mapRemap = usedMaps.Select((old, next) => (old, next)).ToDictionary(x => x.old, x => x.next);
        var playerRemap = usedPlayers.Select((old, next) => (old, next)).ToDictionary(x => x.old, x => x.next);
        var maps = usedMaps.Where(i => i < _cache.Maps.Count).Select(i => _cache.Maps[i]).ToList();
        var players = usedPlayers.Where(i => i < _cache.Players.Count).Select(i => _cache.Players[i]).ToList();
        foreach (var act in _cache.Runs.SelectMany(r => r.Acts))
            act.MapBlobId = act.MapBlobId >= 0 && mapRemap.TryGetValue(act.MapBlobId, out int mapped) ? mapped : -1;
        foreach (var room in _cache.Runs.SelectMany(r => r.Acts).SelectMany(a => a.Rooms))
            room.PlayerBlobIds = room.PlayerBlobIds.Where(playerRemap.ContainsKey).Select(i => playerRemap[i]).ToList();
        _cache.Maps.Clear(); _cache.Maps.AddRange(maps);
        _cache.Players.Clear(); _cache.Players.AddRange(players);
        RebuildHashes();
    }

    private static void RebuildHashes()
    {
        _mapHashes = new Dictionary<string, int>(StringComparer.Ordinal);
        _playerHashes = new Dictionary<string, int>(StringComparer.Ordinal);
        if (_cache == null) return;
        for (int i = 0; i < _cache.Maps.Count; i++)
            _mapHashes[Convert.ToHexString(SHA256.HashData(_cache.Maps[i]))] = i;
        for (int i = 0; i < _cache.Players.Count; i++)
            _playerHashes[Convert.ToHexString(SHA256.HashData(_cache.Players[i]))] = i;
    }

    private static int EncodedSize(StoreData data) => Encoding.UTF8.GetByteCount(Encode(data, out _));

    private static string Encode(StoreData data, out byte[] payload)
    {
        payload = SerializePayload(data);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(payload, 0, payload.Length);
        return Header + Convert.ToBase64String(output.ToArray());
    }

    private static StoreData Decode(string encoded, out byte[]? payload, out bool migrated)
    {
        payload = null;
        migrated = false;
        if (encoded.StartsWith(Header, StringComparison.Ordinal))
        {
            byte[] compressed = Convert.FromBase64String(encoded[Header.Length..]);
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            if (output.Length > 64L * 1024 * 1024) throw new InvalidDataException("snapshot payload exceeds safety limit");
            payload = output.ToArray();
            return DeserializePayload(payload);
        }
        if (encoded.StartsWith(LegacyHeader, StringComparison.Ordinal))
        {
            migrated = true;
            return MigrateLegacy(encoded);
        }
        throw new InvalidDataException("snapshot header is missing or unsupported");
    }

    private static byte[] SerializePayload(StoreData data)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(PayloadMagic);
        writer.Write((byte)SchemaVersion);
        WriteBlobs(writer, data.Maps);
        WriteBlobs(writer, data.Players);
        WriteVarUInt(writer, (uint)data.Runs.Count);
        foreach (var run in data.Runs.OrderBy(r => r.StartTime))
        {
            writer.Write(run.StartTime);
            WriteString(writer, run.Seed);
            writer.Write(run.CapturedAt);
            var acts = run.Acts.OrderBy(a => a.Index).ToList();
            WriteVarUInt(writer, (uint)acts.Count);
            int previousAct = -1;
            foreach (var act in acts)
            {
                WriteVarUInt(writer, (uint)(act.Index - previousAct));
                previousAct = act.Index;
                WriteVarUInt(writer, (uint)(act.MapBlobId + 1));
                var rooms = act.Rooms.OrderBy(r => r.Floor).ToList();
                WriteVarUInt(writer, (uint)rooms.Count);
                int previousFloor = -1;
                foreach (var room in rooms)
                {
                    WriteVarUInt(writer, (uint)(room.Floor - previousFloor));
                    previousFloor = room.Floor;
                    WriteVarInt(writer, room.Row);
                    WriteVarInt(writer, room.Col);
                    WriteVarUInt(writer, (uint)room.Counters.Count);
                    foreach (var counter in room.Counters.OrderBy(kv => (int)kv.Key))
                    {
                        WriteVarUInt(writer, (uint)(int)counter.Key);
                        WriteVarInt(writer, counter.Value);
                    }
                    WriteVarUInt(writer, (uint)room.PlayerBlobIds.Count);
                    foreach (int id in room.PlayerBlobIds) WriteVarUInt(writer, (uint)id);
                }
            }
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static StoreData DeserializePayload(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != PayloadMagic) throw new InvalidDataException("snapshot magic mismatch");
        if (reader.ReadByte() != SchemaVersion) throw new InvalidDataException("snapshot schema mismatch");
        var data = new StoreData();
        ReadBlobs(reader, data.Maps);
        ReadBlobs(reader, data.Players);
        int runCount = ReadCount(reader, "runs");
        for (int ri = 0; ri < runCount; ri++)
        {
            var run = new RunRecord { StartTime = reader.ReadInt64(), Seed = ReadString(reader), CapturedAt = reader.ReadInt64() };
            int actCount = ReadCount(reader, "acts");
            int previousAct = -1;
            for (int ai = 0; ai < actCount; ai++)
            {
                int actIndex = checked(previousAct + (int)ReadVarUInt(reader));
                previousAct = actIndex;
                int mapId = checked((int)ReadVarUInt(reader) - 1);
                if (mapId >= data.Maps.Count) throw new InvalidDataException("map blob reference is invalid");
                var act = new ActRecord { Index = actIndex, MapBlobId = mapId };
                int roomCount = ReadCount(reader, "rooms");
                int previousFloor = -1;
                for (int fi = 0; fi < roomCount; fi++)
                {
                    int floor = checked(previousFloor + (int)ReadVarUInt(reader));
                    previousFloor = floor;
                    var room = new RoomRecord { Floor = floor, Row = ReadVarInt(reader), Col = ReadVarInt(reader) };
                    int counterCount = ReadCount(reader, "RNG counters");
                    for (int ci = 0; ci < counterCount; ci++)
                        room.Counters[(RunRngType)ReadVarUInt(reader)] = ReadVarInt(reader);
                    int playerCount = ReadCount(reader, "player references");
                    for (int pi = 0; pi < playerCount; pi++)
                    {
                        int id = checked((int)ReadVarUInt(reader));
                        if (id >= data.Players.Count) throw new InvalidDataException("player blob reference is invalid");
                        room.PlayerBlobIds.Add(id);
                    }
                    act.Rooms.Add(room);
                }
                run.Acts.Add(act);
            }
            data.Runs.Add(run);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("snapshot payload has trailing data");
        return data;
    }

    private static void WriteBlobs(BinaryWriter writer, List<byte[]> blobs)
    {
        WriteVarUInt(writer, (uint)blobs.Count);
        foreach (var blob in blobs) { WriteVarUInt(writer, (uint)blob.Length); writer.Write(blob); }
    }

    private static void ReadBlobs(BinaryReader reader, List<byte[]> destination)
    {
        int count = ReadCount(reader, "blobs");
        for (int i = 0; i < count; i++)
        {
            int length = checked((int)ReadVarUInt(reader));
            if (length > MaxBlobBytes) throw new InvalidDataException("snapshot blob is too large");
            byte[] blob = reader.ReadBytes(length);
            if (blob.Length != length) throw new EndOfStreamException();
            destination.Add(blob);
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarUInt(writer, (uint)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = checked((int)ReadVarUInt(reader));
        if (length > 4096) throw new InvalidDataException("snapshot string is too long");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteVarInt(BinaryWriter writer, int value)
        => WriteVarUInt(writer, (uint)((value << 1) ^ (value >> 31)));

    private static int ReadVarInt(BinaryReader reader)
    {
        uint value = ReadVarUInt(reader);
        return (int)(value >> 1) ^ -((int)value & 1);
    }

    private static void WriteVarUInt(BinaryWriter writer, uint value)
    {
        while (value >= 0x80) { writer.Write((byte)(value | 0x80)); value >>= 7; }
        writer.Write((byte)value);
    }

    private static uint ReadVarUInt(BinaryReader reader)
    {
        uint result = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            byte value = reader.ReadByte();
            result |= (uint)(value & 0x7f) << shift;
            if ((value & 0x80) == 0) return result;
        }
        throw new InvalidDataException("invalid variable-length integer");
    }

    private static int ReadCount(BinaryReader reader, string label)
    {
        uint count = ReadVarUInt(reader);
        if (count > MaxCollectionCount) throw new InvalidDataException($"snapshot {label} count is invalid");
        return (int)count;
    }

    private static StoreData MigrateLegacy(string encoded)
    {
        byte[] compressed = Convert.FromBase64String(encoded[LegacyHeader.Length..]);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var legacy = JsonSerializer.Deserialize<LegacyStoreData>(gzip, LegacyJsonOptions)
            ?? throw new InvalidDataException("legacy snapshot JSON was empty");
        if (legacy.Version != 4) throw new InvalidDataException($"legacy schema {legacy.Version} is unsupported");

        var data = new StoreData();
        _cache = data;
        RebuildHashes();
        foreach (var item in legacy.Entries)
        {
            if (!TryParseLegacyKey(item.Key, out long start, out string seed, out int actIndex, out int floor)) continue;
            var run = GetOrCreateRun(start, seed);
            run.CapturedAt = Math.Max(run.CapturedAt, item.Value.CapturedAt);
            var room = GetOrCreateRoom(GetOrCreateAct(run, actIndex), floor);
            room.Row = item.Value.Row;
            room.Col = item.Value.Col;
            room.Counters = new Dictionary<RunRngType, int>(item.Value.Counters);
            if (!string.IsNullOrEmpty(item.Value.PlayersJson))
            {
                try
                {
                    var players = JsonSerializer.Deserialize<List<SerializablePlayer>>(
                        item.Value.PlayersJson, JsonSerializationUtility.Options);
                    if (players != null) room.PlayerBlobIds = players.Select(p => AddPlayerBlob(SerializeNative(p))).ToList();
                }
                catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}legacy player migration: {ex.Message}"); }
            }
        }
        return data;
    }

    private static bool TryParseLegacyKey(string key, out long startTime, out string seed, out int actIndex, out int floor)
    {
        startTime = 0; seed = ""; actIndex = floor = 0;
        string[] parts = key.Split('|');
        if (parts.Length != 4) return false;
        return long.TryParse(parts[0], out startTime) && !string.IsNullOrEmpty(seed = parts[1])
            && int.TryParse(parts[2], out actIndex) && int.TryParse(parts[3], out floor);
    }

    private static string ReadRequired(ISaveStore store, string path)
        => store.ReadFile(path) ?? throw new IOException($"snapshot file {path} could not be read");

    private static void DeleteOldTopLevelStore()
    {
        try
        {
            string oldPath = ProjectSettings.GlobalizePath("user://" + OldTopLevelFileName);
            if (!File.Exists(oldPath)) return;
            File.Delete(oldPath);
            GD.Print($"{RetryMod.LogPrefix}removed obsolete top-level RNG snapshot store");
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}remove obsolete RNG snapshot store: {ex.Message}"); }
    }
}
