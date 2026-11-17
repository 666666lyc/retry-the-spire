using System;
using System.IO;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

internal static class SnapshotNativeSerializer
{
    internal static byte[] SerializeNative(SerializableActMap map)
    {
        return Encode(map);
    }

    internal static byte[] SerializeNative(SerializablePlayer player)
    {
        return Encode(player);
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
        return Decode<SerializableActMap>(blob);
    }

    internal static SerializablePlayer DeserializePlayer(byte[] blob)
    {
        return Decode<SerializablePlayer>(blob);
    }

    private static readonly byte[] Header = Encoding.ASCII.GetBytes("RTS-NATIVE-JSON-1\n");
    private static byte[] Encode<T>(T value)
    {
        using var output = new MemoryStream();
        output.Write(Header);
        output.Write(typeof(SerializablePlayer).Assembly.ManifestModule.ModuleVersionId.ToByteArray());
        output.Write(JsonSerializer.SerializeToUtf8Bytes(value, MegaCrit.Sts2.Core.Saves.JsonSerializationUtility.Options));
        return output.ToArray();
    }

    private static T Decode<T>(byte[] blob)
    {
        if (blob.Length <= Header.Length + 16 || !blob.AsSpan().StartsWith(Header)
            || new Guid(blob.AsSpan(Header.Length, 16)) != typeof(SerializablePlayer).Assembly.ManifestModule.ModuleVersionId)
            throw new InvalidDataException("Native blob is from an older/different game build; retaining it and using history reconstruction");
        return JsonSerializer.Deserialize<T>(blob.AsSpan(Header.Length + 16), MegaCrit.Sts2.Core.Saves.JsonSerializationUtility.Options)
            ?? throw new InvalidDataException("Native snapshot JSON was empty");
    }
}
