using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Rngs;

namespace Retry;

internal static class SnapshotCodec
{
    internal const string Header = "RTS-SNAPSHOTS-5\n";
    internal const string LegacyHeader = "RTS-SNAPSHOTS-4\n";
    internal const int SchemaVersion = 5;
    internal const int MaxBlobBytes = 8 * 1024 * 1024;
    internal const int MaxCollectionCount = 1_000_000;
    internal const uint PayloadMagic = 0x35535452; // RTS5
    internal static int EncodedSize(SnapshotData data) => Encoding.UTF8.GetByteCount(Encode(data, out _));
    internal static string Encode(SnapshotData data, out byte[] payload)
    {
        payload = SerializePayload(data);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(payload, 0, payload.Length);
        return Header + Convert.ToBase64String(output.ToArray());
    }

    internal static SnapshotData Decode(string encoded, out byte[]? payload, out bool migrated)
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
            if (output.Length > 64L * 1024 * 1024)
                throw new InvalidDataException("snapshot payload exceeds safety limit");
            payload = output.ToArray();
            return DeserializePayload(payload);
        }

        if (encoded.StartsWith(LegacyHeader, StringComparison.Ordinal))
        {
            migrated = true;
            return SnapshotLegacyMigration.MigrateLegacy(encoded);
        }

        throw new InvalidDataException("snapshot header is missing or unsupported");
    }

    internal static byte[] SerializePayload(SnapshotData data)
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
                    foreach (int id in room.PlayerBlobIds)
                        WriteVarUInt(writer, (uint)id);
                }
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    internal static SnapshotData DeserializePayload(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != PayloadMagic)
            throw new InvalidDataException("snapshot magic mismatch");
        if (reader.ReadByte() != SchemaVersion)
            throw new InvalidDataException("snapshot schema mismatch");
        var data = new SnapshotData();
        ReadBlobs(reader, data.Maps);
        ReadBlobs(reader, data.Players);
        int runCount = ReadCount(reader, "runs");
        for (int ri = 0; ri < runCount; ri++)
        {
            var run = new SnapshotRunRecord
            {
                StartTime = reader.ReadInt64(),
                Seed = ReadString(reader),
                CapturedAt = reader.ReadInt64()
            };
            int actCount = ReadCount(reader, "acts");
            int previousAct = -1;
            for (int ai = 0; ai < actCount; ai++)
            {
                int actIndex = checked(previousAct + (int)ReadVarUInt(reader));
                previousAct = actIndex;
                int mapId = checked((int)ReadVarUInt(reader) - 1);
                if (mapId >= data.Maps.Count)
                    throw new InvalidDataException("map blob reference is invalid");
                var act = new SnapshotActRecord
                {
                    Index = actIndex,
                    MapBlobId = mapId
                };
                int roomCount = ReadCount(reader, "rooms");
                int previousFloor = -1;
                for (int fi = 0; fi < roomCount; fi++)
                {
                    int floor = checked(previousFloor + (int)ReadVarUInt(reader));
                    previousFloor = floor;
                    var room = new SnapshotRoomRecord
                    {
                        Floor = floor,
                        Row = ReadVarInt(reader),
                        Col = ReadVarInt(reader)
                    };
                    int counterCount = ReadCount(reader, "RNG counters");
                    for (int ci = 0; ci < counterCount; ci++)
                        room.Counters[(RunRngType)ReadVarUInt(reader)] = ReadVarInt(reader);
                    int playerCount = ReadCount(reader, "player references");
                    for (int pi = 0; pi < playerCount; pi++)
                    {
                        int id = checked((int)ReadVarUInt(reader));
                        if (id >= data.Players.Count)
                            throw new InvalidDataException("player blob reference is invalid");
                        room.PlayerBlobIds.Add(id);
                    }

                    act.Rooms.Add(room);
                }

                run.Acts.Add(act);
            }

            data.Runs.Add(run);
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException("snapshot payload has trailing data");
        return data;
    }

    internal static void WriteBlobs(BinaryWriter writer, List<byte[]> blobs)
    {
        WriteVarUInt(writer, (uint)blobs.Count);
        foreach (var blob in blobs)
        {
            WriteVarUInt(writer, (uint)blob.Length);
            writer.Write(blob);
        }
    }

    internal static void ReadBlobs(BinaryReader reader, List<byte[]> destination)
    {
        int count = ReadCount(reader, "blobs");
        for (int i = 0; i < count; i++)
        {
            int length = checked((int)ReadVarUInt(reader));
            if (length > MaxBlobBytes)
                throw new InvalidDataException("snapshot blob is too large");
            byte[] blob = reader.ReadBytes(length);
            if (blob.Length != length)
                throw new EndOfStreamException();
            destination.Add(blob);
        }
    }

    internal static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarUInt(writer, (uint)bytes.Length);
        writer.Write(bytes);
    }

    internal static string ReadString(BinaryReader reader)
    {
        int length = checked((int)ReadVarUInt(reader));
        if (length > 4096)
            throw new InvalidDataException("snapshot string is too long");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    internal static void WriteVarInt(BinaryWriter writer, int value) => WriteVarUInt(writer, (uint)((value << 1) ^ (value >> 31)));
    internal static int ReadVarInt(BinaryReader reader)
    {
        uint value = ReadVarUInt(reader);
        return (int)(value >> 1) ^ -((int)value & 1);
    }

    internal static void WriteVarUInt(BinaryWriter writer, uint value)
    {
        while (value >= 0x80)
        {
            writer.Write((byte)(value | 0x80));
            value >>= 7;
        }

        writer.Write((byte)value);
    }

    internal static uint ReadVarUInt(BinaryReader reader)
    {
        uint result = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            byte value = reader.ReadByte();
            result |= (uint)(value & 0x7f) << shift;
            if ((value & 0x80) == 0)
                return result;
        }

        throw new InvalidDataException("invalid variable-length integer");
    }

    internal static int ReadCount(BinaryReader reader, string label)
    {
        uint count = ReadVarUInt(reader);
        if (count > MaxCollectionCount)
            throw new InvalidDataException($"snapshot {label} count is invalid");
        return (int)count;
    }
}
