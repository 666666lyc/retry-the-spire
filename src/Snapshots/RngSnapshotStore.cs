// Compact, versioned room-entry snapshots stored through the game's ISaveStore.
// Maps and players use native packet serializers, are deduplicated, and the
// complete binary payload is gzip-compressed before its text-safe wrapper.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Godot;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

public static class RngSnapshotStore
{
    internal static readonly FieldInfo? RunStartTimeField = typeof(RunManager).GetField("_startTime", BindingFlags.Instance | BindingFlags.NonPublic);
    public static void Capture(long startTime, string seed, int actIndex, int floor, MapCoord coord, Dictionary<RunRngType, int> counters, IReadOnlyList<SerializablePlayer>? players = null, ActMap? map = null)
    {
        SnapshotPersistence.EnsureLoaded();
        if (SnapshotPersistence._cache == null || startTime <= 0)
            return;
        var run = SnapshotIndex.GetOrCreateRun(startTime, seed);
        var act = SnapshotIndex.GetOrCreateAct(run, actIndex);
        var room = SnapshotIndex.GetOrCreateRoom(act, floor);
        room.Row = coord.row;
        room.Col = coord.col;
        room.Counters = new Dictionary<RunRngType, int>(counters);
        run.CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (players != null)
        {
            room.PlayerBlobIds = new List<int>(players.Count);
            foreach (var player in players)
            {
                try
                {
                    room.PlayerBlobIds.Add(SnapshotIndex.AddPlayerBlob(SnapshotNativeSerializer.SerializeNative(player)));
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}snapshot player serialize: {ex.Message}");
                }
            }
        }

        map ??= TryGetLiveMap(actIndex);
        if (map != null)
        {
            try
            {
                act.MapBlobId = SnapshotIndex.AddMapBlob(SnapshotNativeSerializer.SerializeNative(SerializableActMap.FromActMap(map)));
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}snapshot map serialize: {ex.Message}");
            }
        }

        SnapshotPersistence.TrySave();
    }

    public static long GetCurrentRunStartTime() => RunStartTimeField?.GetValue(RunManager.Instance)is long value ? value : 0;
    public static void CaptureCoordinates(long startTime, string seed, int actIndex, IReadOnlyList<MapCoord> coords)
    {
        SnapshotPersistence.EnsureLoaded();
        if (SnapshotPersistence._cache == null || startTime <= 0)
            return;
        var run = SnapshotIndex.GetOrCreateRun(startTime, seed);
        var act = SnapshotIndex.GetOrCreateAct(run, actIndex);
        for (int floor = 0; floor < coords.Count; floor++)
        {
            var room = SnapshotIndex.GetOrCreateRoom(act, floor);
            room.Row = coords[floor].row;
            room.Col = coords[floor].col;
        }

        run.CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SnapshotPersistence.TrySave();
    }

    // The old name is retained because callers also need RNG/player/map refs.
    public static void CopyExactCoordinates(long sourceStartTime, long destinationStartTime, string seed, int actIndex, int floorCount)
    {
        SnapshotPersistence.EnsureLoaded();
        if (SnapshotPersistence._cache == null || sourceStartTime <= 0 || destinationStartTime <= 0)
            return;
        var sourceAct = SnapshotIndex.FindRun(sourceStartTime, seed)?.Acts.FirstOrDefault(a => a.Index == actIndex);
        if (sourceAct == null)
            return;
        var destination = SnapshotIndex.GetOrCreateRun(destinationStartTime, seed);
        var destinationAct = SnapshotIndex.GetOrCreateAct(destination, actIndex);
        if (sourceAct.MapBlobId >= 0)
            destinationAct.MapBlobId = sourceAct.MapBlobId;
        bool changed = false;
        foreach (var source in sourceAct.Rooms.Where(r => r.Floor < floorCount))
        {
            var room = SnapshotIndex.GetOrCreateRoom(destinationAct, source.Floor);
            room.Row = source.Row;
            room.Col = source.Col;
            room.Counters = new Dictionary<RunRngType, int>(source.Counters);
            room.PlayerBlobIds = new List<int>(source.PlayerBlobIds);
            changed = true;
        }

        if (changed || sourceAct.MapBlobId >= 0)
        {
            destination.CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SnapshotPersistence.TrySave();
        }
    }

    public static MapCoord? TryGetExactCoord(long startTime, string seed, int actIndex, int floor)
    {
        SnapshotPersistence.EnsureLoaded();
        var room = SnapshotIndex.FindRoom(startTime, seed, actIndex, floor);
        return room == null ? null : new MapCoord
        {
            row = room.Row,
            col = room.Col
        };
    }

    public static MapCoord? TryGetLegacyCoord(string seed, int actIndex, int floor) => null;
    public static MapCoord? TryGetCoord(long startTime, string seed, int actIndex, int floor, bool allowLegacy = true) => TryGetExactCoord(startTime, seed, actIndex, floor);
    public static SerializablePlayer? TryGetPlayerSnapshot(long startTime, string seed, int actIndex, int floor, ulong playerId)
    {
        SnapshotPersistence.EnsureLoaded();
        if (SnapshotPersistence._cache == null)
            return null;
        var room = SnapshotIndex.FindRoom(startTime, seed, actIndex, floor);
        if (room == null)
            return null;
        foreach (int id in room.PlayerBlobIds)
        {
            if (id < 0 || id >= SnapshotPersistence._cache.Players.Count)
                continue;
            try
            {
                var player = SnapshotNativeSerializer.DeserializePlayer(SnapshotPersistence._cache.Players[id]);
                if (player.NetId == playerId)
                    return player;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}snapshot player deserialize: {ex.Message}");
            }
        }

        return null;
    }

    public static bool TryGetActMap(long startTime, string seed, int actIndex, out SerializableActMap? map)
    {
        SnapshotPersistence.EnsureLoaded();
        map = null;
        if (SnapshotPersistence._cache == null)
            return false;
        var act = SnapshotIndex.FindRun(startTime, seed)?.Acts.FirstOrDefault(a => a.Index == actIndex);
        if (act == null || act.MapBlobId < 0 || act.MapBlobId >= SnapshotPersistence._cache.Maps.Count)
            return false;
        try
        {
            map = SnapshotNativeSerializer.DeserializeMap(SnapshotPersistence._cache.Maps[act.MapBlobId]);
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot map deserialize act={actIndex}: {ex.Message}");
            return false;
        }
    }

    public static ActMap? TryCreateSavedActMap(long startTime, string seed, int actIndex) => TryGetActMap(startTime, seed, actIndex, out var map) && map != null ? new SavedActMap(map) : null;
    public static bool QueueSavedMapForGeneration(long startTime, string seed, int actIndex)
    {
        if (!TryGetActMap(startTime, seed, actIndex, out var map) || map == null)
            return false;
        var queued = RunManager.Instance.SavedMapsToLoad ?? new Dictionary<int, SerializableActMap>();
        queued[actIndex] = map;
        RunManager.Instance.SavedMapsToLoad = queued;
        GD.Print($"{RetryMod.LogPrefix}queued exact saved map act={actIndex}");
        return true;
    }

    public static bool TryApplyLive(RunRngSet liveRng, long startTime, string seed, int actIndex, int floor)
    {
        SnapshotPersistence.EnsureLoaded();
        var room = SnapshotIndex.FindRoom(startTime, seed, actIndex, floor);
        if (room == null)
            return false;
        var save = new SerializableRunRngSet
        {
            Seed = seed
        };
        foreach (var kv in room.Counters)
            save.Counters[kv.Key] = kv.Value;
        try
        {
            liveRng.LoadFromSerializable(save);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot apply: {ex.Message}");
            return false;
        }

        return true;
    }

    public static bool HasSnapshot(long startTime, string seed, int actIndex, int floor)
    {
        SnapshotPersistence.EnsureLoaded();
        return SnapshotIndex.FindRoom(startTime, seed, actIndex, floor) != null;
    }

    public static bool HasMapSnapshot(long startTime, string seed, int actIndex)
    {
        SnapshotPersistence.EnsureLoaded();
        var act = SnapshotIndex.FindRun(startTime, seed)?.Acts.FirstOrDefault(a => a.Index == actIndex);
        return SnapshotPersistence._cache != null && act?.MapBlobId >= 0 && act.MapBlobId < SnapshotPersistence._cache.Maps.Count;
    }

    public static string Fingerprint(ActMap map) => Convert.ToHexString(SHA256.HashData(SnapshotNativeSerializer.SerializeNative(SerializableActMap.FromActMap(map))));
    public static string Fingerprint(SerializablePlayer player) => Convert.ToHexString(SHA256.HashData(SnapshotNativeSerializer.SerializeNative(player)));
    public static int GetEncodedSizeForDiagnostics()
    {
        SnapshotPersistence.EnsureLoaded();
        return SnapshotPersistence._cache == null ? 0 : SnapshotCodec.EncodedSize(SnapshotPersistence._cache);
    }

    internal static ActMap? TryGetLiveMap(int actIndex)
    {
        try
        {
            var manager = RunManager.Instance;
            var state = manager == null ? null : StateAccessor.GetState(manager);
            return state != null && state.CurrentActIndex == actIndex ? state.Map : null;
        }
        catch
        {
            return null;
        }
    }
}
