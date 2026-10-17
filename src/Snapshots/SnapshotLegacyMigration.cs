using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

internal static class SnapshotLegacyMigration
{
    internal static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new JsonStringEnumConverter()
        },
    };
    internal static SnapshotData MigrateLegacy(string encoded)
    {
        byte[] compressed = Convert.FromBase64String(encoded[SnapshotCodec.LegacyHeader.Length..]);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var legacy = JsonSerializer.Deserialize<LegacySnapshotData>(gzip, LegacyJsonOptions) ?? throw new InvalidDataException("legacy snapshot JSON was empty");
        if (legacy.Version != 4)
            throw new InvalidDataException($"legacy schema {legacy.Version} is unsupported");
        var data = new SnapshotData();
        SnapshotPersistence._cache = data;
        SnapshotIndex.RebuildHashes();
        foreach (var item in legacy.Entries)
        {
            if (!TryParseLegacyKey(item.Key, out long start, out string seed, out int actIndex, out int floor))
                continue;
            var run = SnapshotIndex.GetOrCreateRun(start, seed);
            run.CapturedAt = Math.Max(run.CapturedAt, item.Value.CapturedAt);
            var room = SnapshotIndex.GetOrCreateRoom(SnapshotIndex.GetOrCreateAct(run, actIndex), floor);
            room.Row = item.Value.Row;
            room.Col = item.Value.Col;
            room.Counters = new Dictionary<RunRngType, int>(item.Value.Counters);
            if (!string.IsNullOrEmpty(item.Value.PlayersJson))
            {
                try
                {
                    var players = JsonSerializer.Deserialize<List<SerializablePlayer>>(item.Value.PlayersJson, JsonSerializationUtility.Options);
                    if (players != null)
                        room.PlayerBlobIds = players.Select(p => SnapshotIndex.AddPlayerBlob(SnapshotNativeSerializer.SerializeNative(p))).ToList();
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}legacy player migration: {ex.Message}");
                }
            }
        }

        return data;
    }

    internal static bool TryParseLegacyKey(string key, out long startTime, out string seed, out int actIndex, out int floor)
    {
        startTime = 0;
        seed = "";
        actIndex = floor = 0;
        string[] parts = key.Split('|');
        if (parts.Length != 4)
            return false;
        return long.TryParse(parts[0], out startTime) && !string.IsNullOrEmpty(seed = parts[1]) && int.TryParse(parts[2], out actIndex) && int.TryParse(parts[3], out floor);
    }
}
