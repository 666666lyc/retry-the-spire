using System;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;

namespace Retry;

internal static class RestartPayloadSerializer
{
    internal static void SendControl(INetGameService service, ulong target, RestartControlPacket message)
    {
        var writer = new PacketWriter();
        writer.WriteByte(message.Protocol);
        writer.WriteByte((byte)message.Kind);
        writer.WriteUInt(message.Generation, 32);
        writer.WriteULong(message.NonceHigh, 64);
        writer.WriteULong(message.NonceLow, 64);
        writer.WriteULong(message.HostId, 64);
        writer.WriteULong(message.ActorId, 64);
        byte[] payload = FinishPayload(writer);
        if (payload.Length != RestartEnvelope.ControlPayloadSize)
            throw new InvalidOperationException($"restart control payload has unexpected length {payload.Length}");
        RestartPacketSender.SendRaw(service, target, RestartEnvelope.BuildEnvelope(RestartRawMessageType.Control, payload));
    }

    internal static void SendDirect(INetGameService service, ulong target, DirectRestartPayload message)
    {
        var writer = new PacketWriter();
        writer.WriteUInt(message.Generation, 32);
        writer.WriteULong(message.NonceHigh, 64);
        writer.WriteULong(message.NonceLow, 64);
        writer.WriteULong(message.HostId, 64);
        writer.WriteULong(message.ActorId, 64);
        writer.Write(message.Run);
        RestartPacketSender.SendRaw(service, target, RestartEnvelope.BuildEnvelope(RestartRawMessageType.DirectSave, FinishPayload(writer)));
    }

    internal static void Dispatch(INetGameService service, ulong senderId, RestartRawMessageType messageType, byte[] payload)
    {
        try
        {
            var reader = new PacketReader();
            reader.Reset(payload);
            switch (messageType)
            {
                case RestartRawMessageType.Control:
                    byte protocol = reader.ReadByte();
                    if (protocol != RestartControlPacket.CurrentProtocol)
                        throw new InvalidOperationException($"unsupported control protocol {protocol}");
                    var kind = (RestartControlKind)reader.ReadByte();
                    if (!Enum.IsDefined(kind))
                        throw new InvalidOperationException($"unknown control kind {(byte)kind}");
                    MultiplayerRestartCoordinator.OnRawControl(service, new RestartControlPacket { Protocol = protocol, Kind = kind, Generation = reader.ReadUInt(32), NonceHigh = reader.ReadULong(64), NonceLow = reader.ReadULong(64), HostId = reader.ReadULong(64), ActorId = reader.ReadULong(64), }, senderId);
                    break;
                case RestartRawMessageType.DirectSave:
                    if (service is not NetClientGameService client || senderId == 0 || senderId != client.HostNetId)
                        throw new InvalidOperationException($"direct-save sender {senderId} is not the current host");
                    MultiplayerRestartCoordinator.OnRawDirectPayload(service, new DirectRestartPayload { Generation = reader.ReadUInt(32), NonceHigh = reader.ReadULong(64), NonceLow = reader.ReadULong(64), HostId = reader.ReadULong(64), ActorId = reader.ReadULong(64), Run = reader.Read<SerializableRun>(), }, senderId);
                    break;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}restart raw payload rejected sender={senderId} " + $"type={messageType}: {ex.GetType().Name}:{ex.Message}");
        }
    }

    internal static byte[] FinishPayload(PacketWriter writer)
    {
        writer.ZeroByteRemainder();
        int length = checked((writer.BitPosition + 7) / 8);
        if (length <= 0)
            throw new InvalidOperationException("restart payload is empty");
        var payload = new byte[length];
        Buffer.BlockCopy(writer.Buffer, 0, payload, 0, length);
        return payload;
    }
}
