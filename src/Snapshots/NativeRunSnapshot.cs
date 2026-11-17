using System;
using System.IO;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Retry;

// A tagged native run is an additional opaque blob in the v5 player blob pool.
// The v5 layout, deduplication, retention and backup rules remain unchanged.
// Full native saves include xoshiro state (counters alone cannot restore it),
// mode flags, player-specific state, room queues, maps and the room-entry timer.
internal static class NativeRunSnapshot
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("RTS-NATIVE-RUN-1\n");
    internal static bool IsRun(byte[] blob) => blob.AsSpan().StartsWith(Header);

    internal static byte[] Encode(SerializableRun save)
    {
        using var stream = new MemoryStream();
        stream.Write(Header);
        stream.Write(typeof(RunState).Assembly.ManifestModule.ModuleVersionId.ToByteArray());
        stream.Write(Encoding.UTF8.GetBytes(JsonSerializationUtility.ToJson(save)));
        if (stream.Length > SnapshotCodec.MaxBlobBytes)
            throw new InvalidDataException("native room-entry snapshot exceeds safety limit");
        return stream.ToArray();
    }

    internal static SerializableRun Decode(byte[] blob)
    {
        if (!IsRun(blob) || blob.Length <= Header.Length + 16)
            throw new InvalidDataException("native snapshot header is missing or truncated");
        var source = new Guid(blob.AsSpan(Header.Length, 16));
        if (source != typeof(RunState).Assembly.ManifestModule.ModuleVersionId)
            throw new InvalidDataException("native room snapshot belongs to a different game build; original data retained");
        return JsonSerializer.Deserialize<SerializableRun>(blob.AsSpan(Header.Length + 16), JsonSerializationUtility.Options)
            ?? throw new InvalidDataException("native room snapshot was empty");
    }

    internal static SerializableRun? Find(long start, string seed, int act, int floor)
    {
        SnapshotPersistence.EnsureLoaded();
        var room = SnapshotIndex.FindRoom(start, seed, act, floor);
        if (room == null || SnapshotPersistence._cache == null)
            return null;
        foreach (int id in room.PlayerBlobIds)
        {
            var blob = SnapshotPersistence._cache.Players[id];
            if (!IsRun(blob))
                continue;
            var save = Decode(blob); // A present but invalid snapshot must not silently become a new run.
            if (save.StartTime != start || save.SerializableRng.Seed != seed || save.CurrentActIndex != act || save.VisitedMapCoords.Count == 0)
                throw new InvalidDataException("native room snapshot identity is invalid");
            var coord = save.VisitedMapCoords[^1];
            if (coord.row != room.Row || coord.col != room.Col)
                throw new InvalidDataException("native room snapshot coordinate is invalid");
            return save;
        }
        return null;
    }

    internal static void ValidateForRestore(long start, string seed, int act, int floor)
    {
        var save = Find(start, seed, act, floor);
        if (save == null)
            return;
        GameCompatibility.PrepareHistoryMode(save);
        _ = RunState.FromSerializable(save);
    }
}
