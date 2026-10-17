using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Godot;
using MegaCrit.Sts2.Core.Saves;

namespace Retry;

internal static class SnapshotIndex
{
    internal static Dictionary<string, int> _mapHashes = new(StringComparer.Ordinal);
    internal static Dictionary<string, int> _playerHashes = new(StringComparer.Ordinal);
    internal static SnapshotRunRecord? FindRun(long startTime, string seed) => SnapshotPersistence._cache?.Runs.FirstOrDefault(r => r.StartTime == startTime && r.Seed == seed);
    internal static SnapshotRoomRecord? FindRoom(long startTime, string seed, int actIndex, int floor) => FindRun(startTime, seed)?.Acts.FirstOrDefault(a => a.Index == actIndex)?.Rooms.FirstOrDefault(r => r.Floor == floor);
    internal static SnapshotRunRecord GetOrCreateRun(long startTime, string seed)
    {
        var existing = FindRun(startTime, seed);
        if (existing != null)
            return existing;
        var run = new SnapshotRunRecord
        {
            StartTime = startTime,
            Seed = seed
        };
        SnapshotPersistence._cache!.Runs.Add(run);
        return run;
    }

    internal static SnapshotActRecord GetOrCreateAct(SnapshotRunRecord run, int index)
    {
        var existing = run.Acts.FirstOrDefault(a => a.Index == index);
        if (existing != null)
            return existing;
        var act = new SnapshotActRecord
        {
            Index = index
        };
        run.Acts.Add(act);
        return act;
    }

    internal static SnapshotRoomRecord GetOrCreateRoom(SnapshotActRecord act, int floor)
    {
        var existing = act.Rooms.FirstOrDefault(r => r.Floor == floor);
        if (existing != null)
            return existing;
        var room = new SnapshotRoomRecord
        {
            Floor = floor
        };
        act.Rooms.Add(room);
        return room;
    }

    internal static int AddMapBlob(byte[] blob) => AddBlob(SnapshotPersistence._cache!.Maps, _mapHashes, blob);
    internal static int AddPlayerBlob(byte[] blob) => AddBlob(SnapshotPersistence._cache!.Players, _playerHashes, blob);
    internal static int AddBlob(List<byte[]> blobs, Dictionary<string, int> hashes, byte[] blob)
    {
        string hash = Convert.ToHexString(SHA256.HashData(blob));
        if (hashes.TryGetValue(hash, out int id) && id >= 0 && id < blobs.Count && blobs[id].AsSpan().SequenceEqual(blob))
            return id;
        id = blobs.Count;
        blobs.Add(blob);
        hashes[hash] = id;
        return id;
    }

    internal static void PruneToLimit(int targetBytes)
    {
        if (SnapshotPersistence._cache == null || SnapshotPersistence._cache.Runs.Count == 0)
            return;
        long currentStart = RngSnapshotStore.GetCurrentRunStartTime();
        var historyStarts = new HashSet<long>();
        try
        {
            foreach (string name in SaveManager.Instance.GetAllRunHistoryNames())
                if (long.TryParse(Path.GetFileNameWithoutExtension(name), out long value))
                    historyStarts.Add(value);
        }
        catch
        {
        }

        SnapshotRetention.Prune(SnapshotPersistence._cache, targetBytes, currentStart, historyStarts, run => GD.Print($"{RetryMod.LogPrefix}snapshot prune run startTime={run.StartTime} hasHistory={historyStarts.Contains(run.StartTime)}"));
        GarbageCollectBlobs();
    }

    internal static void GarbageCollectBlobs()
    {
        if (SnapshotPersistence._cache == null)
            return;
        var usedMaps = SnapshotPersistence._cache.Runs.SelectMany(r => r.Acts).Where(a => a.MapBlobId >= 0).Select(a => a.MapBlobId).Distinct().OrderBy(i => i).ToList();
        var usedPlayers = SnapshotPersistence._cache.Runs.SelectMany(r => r.Acts).SelectMany(a => a.Rooms).SelectMany(r => r.PlayerBlobIds).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        var mapRemap = usedMaps.Select((old, next) => (old, next)).ToDictionary(x => x.old, x => x.next);
        var playerRemap = usedPlayers.Select((old, next) => (old, next)).ToDictionary(x => x.old, x => x.next);
        var maps = usedMaps.Where(i => i < SnapshotPersistence._cache.Maps.Count).Select(i => SnapshotPersistence._cache.Maps[i]).ToList();
        var players = usedPlayers.Where(i => i < SnapshotPersistence._cache.Players.Count).Select(i => SnapshotPersistence._cache.Players[i]).ToList();
        foreach (var act in SnapshotPersistence._cache.Runs.SelectMany(r => r.Acts))
            act.MapBlobId = act.MapBlobId >= 0 && mapRemap.TryGetValue(act.MapBlobId, out int mapped) ? mapped : -1;
        foreach (var room in SnapshotPersistence._cache.Runs.SelectMany(r => r.Acts).SelectMany(a => a.Rooms))
            room.PlayerBlobIds = room.PlayerBlobIds.Where(playerRemap.ContainsKey).Select(i => playerRemap[i]).ToList();
        SnapshotPersistence._cache.Maps.Clear();
        SnapshotPersistence._cache.Maps.AddRange(maps);
        SnapshotPersistence._cache.Players.Clear();
        SnapshotPersistence._cache.Players.AddRange(players);
        RebuildHashes();
    }

    internal static void RebuildHashes()
    {
        _mapHashes = new Dictionary<string, int>(StringComparer.Ordinal);
        _playerHashes = new Dictionary<string, int>(StringComparer.Ordinal);
        if (SnapshotPersistence._cache == null)
            return;
        for (int i = 0; i < SnapshotPersistence._cache.Maps.Count; i++)
            _mapHashes[Convert.ToHexString(SHA256.HashData(SnapshotPersistence._cache.Maps[i]))] = i;
        for (int i = 0; i < SnapshotPersistence._cache.Players.Count; i++)
            _playerHashes[Convert.ToHexString(SHA256.HashData(SnapshotPersistence._cache.Players[i]))] = i;
    }
}
