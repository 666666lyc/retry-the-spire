using System;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

internal static class SnapshotNativeSerializer
{
    internal static byte[] SerializeNative(SerializableActMap map)
    {
        var writer = new PacketWriter();
        map.Serialize(writer);
        return CopyWrittenBytes(writer);
    }

    internal static byte[] SerializeNative(SerializablePlayer player)
    {
        var writer = new PacketWriter();
        player.Serialize(writer);
        return CopyWrittenBytes(writer);
    }

    internal static byte[] CopyWrittenBytes(PacketWriter writer)
    {
        writer.ZeroByteRemainder();
        var result = new byte[writer.BytePosition];
        Buffer.BlockCopy(writer.Buffer, 0, result, 0, result.Length);
        return result;
    }

    internal static SerializableActMap DeserializeMap(byte[] blob)
    {
        var result = new SerializableActMap();
        var reader = new PacketReader();
        reader.Reset(blob);
        result.Deserialize(reader);
        return result;
    }

    internal static SerializablePlayer DeserializePlayer(byte[] blob)
    {
        var result = new SerializablePlayer();
        var reader = new PacketReader();
        reader.Reset(blob);
        result.Deserialize(reader);
        return result;
    }
}
