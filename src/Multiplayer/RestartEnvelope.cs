using System;
using System.Buffers.Binary;
using System.Linq;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace Retry;

internal static class RestartEnvelope
{
    internal const byte WireVersion = 1;
    internal const int HeaderSize = 24;
    internal const int ControlPayloadSize = 38;
    internal const int MaxPacketSize = 8 * 1024 * 1024;
    internal const int ReliableChannel = 0;
    internal const int SteamUnspecifiedChannel = -1;
    // Random, fixed 128-bit discriminator. A complete match transfers packet
    // ownership to Retry; malformed Retry packets are dropped, never offered
    // to NetMessageBus as a possible vanilla message.
    internal static readonly byte[] Magic = [0xA7, 0x51, 0x92, 0x0C, 0x64, 0xDA, 0x4E, 0x39, 0xB8, 0x17, 0xF2, 0x45, 0x6B, 0xC0, 0x9D, 0x73, ];
    internal static byte[] BuildEnvelope(RestartRawMessageType messageType, byte[] payload)
    {
        int totalLength = checked(HeaderSize + payload.Length);
        if (totalLength > MaxPacketSize)
            throw new InvalidOperationException($"restart packet exceeds {MaxPacketSize} bytes");
        var packet = new byte[totalLength];
        Magic.CopyTo(packet, 0);
        packet[16] = WireVersion;
        packet[17] = (byte)messageType;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(18, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), (uint)payload.Length);
        payload.CopyTo(packet, HeaderSize);
        return packet;
    }

    internal static bool HasMagic(byte[] packetBytes) => packetBytes.Length >= Magic.Length && packetBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic);
    // SteamNetworkingSockets ignores the channel argument when sending and
    // reports m_nChannel as -1 on receive. ENet preserves the requested
    // reliable channel 0, so accept both representations of the same route.
    internal static bool IsExpectedReceiveChannel(int channel) => channel is ReliableChannel or SteamUnspecifiedChannel;
    // Call only after HasMagic: validation order matches the original receive path.
    internal static int Validate(byte[] packetBytes, NetTransferMode mode, int channel)
    {
        if (packetBytes.Length < HeaderSize)
            throw new InvalidOperationException("truncated raw packet header");
        if (packetBytes.Length > MaxPacketSize)
            throw new InvalidOperationException($"raw packet exceeds {MaxPacketSize} bytes");
        if (mode != NetTransferMode.Reliable)
            throw new InvalidOperationException($"raw packet used non-reliable mode {mode}");
        if (!IsExpectedReceiveChannel(channel))
            throw new InvalidOperationException($"raw packet used unexpected channel {channel}");
        if (packetBytes[16] != WireVersion)
            throw new InvalidOperationException($"unsupported wire version {packetBytes[16]}");
        if (BinaryPrimitives.ReadUInt16LittleEndian(packetBytes.AsSpan(18, 2)) != 0)
            throw new InvalidOperationException("raw packet reserved field is non-zero");
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(packetBytes.AsSpan(20, 4));
        int actualLength = packetBytes.Length - HeaderSize;
        if (declaredLength != (uint)actualLength)
            throw new InvalidOperationException($"raw packet length mismatch declared={declaredLength} actual={actualLength}");
        var messageType = (RestartRawMessageType)packetBytes[17];
        if (messageType == RestartRawMessageType.Control && actualLength != ControlPayloadSize)
            throw new InvalidOperationException($"control payload length is {actualLength}, expected {ControlPayloadSize}");
        if (messageType == RestartRawMessageType.DirectSave && actualLength <= 36)
            throw new InvalidOperationException("direct-save payload is truncated");
        if (messageType is not (RestartRawMessageType.Control or RestartRawMessageType.DirectSave))
            throw new InvalidOperationException($"unknown raw message type {(byte)messageType}");
        return actualLength;
    }
}
