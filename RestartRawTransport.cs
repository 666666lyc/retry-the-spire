using System;
using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Steamworks;
using GameSteamClient = MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient;
using GameSteamHost = MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamHost;

namespace Retry;

internal enum RestartRawMessageType : byte
{
    Control = 1,
    DirectSave = 2,
}

internal sealed class RestartControlPacket
{
    internal const byte CurrentProtocol = 1;

    internal byte Protocol = CurrentProtocol;
    internal RestartControlKind Kind;
    internal uint Generation;
    internal ulong NonceHigh;
    internal ulong NonceLow;
    internal ulong HostId;
    internal ulong ActorId;
}

internal sealed class DirectRestartPayload
{
    internal uint Generation;
    internal ulong NonceHigh;
    internal ulong NonceLow;
    internal ulong HostId;
    internal ulong ActorId;
    internal SerializableRun Run = null!;
}

/// <summary>
/// A private side-band protocol that bypasses the game's generated message-id
/// table. Packets are sent only after the vanilla initial-info handshake has
/// proved that the other endpoint is running Retry 0.5.0 or newer.
/// </summary>
internal static class RestartRawTransport
{
    private const byte WireVersion = 1;
    private const int HeaderSize = 24;
    private const int ControlPayloadSize = 38;
    private const int MaxPacketSize = 8 * 1024 * 1024;
    private const int ReliableChannel = 0;
    private const int SteamUnspecifiedChannel = -1;

    // Random, fixed 128-bit discriminator. A complete match transfers packet
    // ownership to Retry; malformed Retry packets are dropped, never offered
    // to NetMessageBus as a possible vanilla message.
    private static readonly byte[] Magic =
    [
        0xA7, 0x51, 0x92, 0x0C, 0x64, 0xDA, 0x4E, 0x39,
        0xB8, 0x17, 0xF2, 0x45, 0x6B, 0xC0, 0x9D, 0x73,
    ];

    private static readonly FieldInfo? HostTransportField = typeof(NetHostGameService).GetField(
        "_netHost", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? SteamClientConnectionField = typeof(GameSteamClient).GetField(
        "_conn", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo? SteamHostGetConnectionMethod = typeof(GameSteamHost).GetMethod(
        "GetConnectionForNetId", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? SteamHostConnectionField = typeof(GameSteamHost)
        .GetNestedType("ClientConnection", BindingFlags.NonPublic)?
        .GetField("conn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    internal static void SendControl(
        INetGameService service,
        ulong target,
        RestartControlPacket message)
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
        if (payload.Length != ControlPayloadSize)
            throw new InvalidOperationException(
                $"restart control payload has unexpected length {payload.Length}");
        SendRaw(service, target, BuildEnvelope(RestartRawMessageType.Control, payload));
    }

    internal static void SendDirect(
        INetGameService service,
        ulong target,
        DirectRestartPayload message)
    {
        var writer = new PacketWriter();
        writer.WriteUInt(message.Generation, 32);
        writer.WriteULong(message.NonceHigh, 64);
        writer.WriteULong(message.NonceLow, 64);
        writer.WriteULong(message.HostId, 64);
        writer.WriteULong(message.ActorId, 64);
        writer.Write(message.Run);
        SendRaw(
            service,
            target,
            BuildEnvelope(RestartRawMessageType.DirectSave, FinishPayload(writer)));
    }

    /// <summary>Returns true when the packet belongs to Retry and was consumed.</summary>
    internal static bool TryConsume(
        INetGameService service,
        ulong senderId,
        byte[] packetBytes,
        NetTransferMode mode,
        int channel)
    {
        if (!HasMagic(packetBytes)) return false;

        try
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
                throw new InvalidOperationException(
                    $"raw packet length mismatch declared={declaredLength} actual={actualLength}");

            var messageType = (RestartRawMessageType)packetBytes[17];
            if (messageType == RestartRawMessageType.Control && actualLength != ControlPayloadSize)
                throw new InvalidOperationException(
                    $"control payload length is {actualLength}, expected {ControlPayloadSize}");
            if (messageType == RestartRawMessageType.DirectSave && actualLength <= 36)
                throw new InvalidOperationException("direct-save payload is truncated");
            if (messageType is not (RestartRawMessageType.Control or RestartRawMessageType.DirectSave))
                throw new InvalidOperationException($"unknown raw message type {(byte)messageType}");

            GD.Print(
                $"{RetryMod.LogPrefix}restart raw-envelope-received localType={service.Type} " +
                $"local={service.NetId} sender={senderId} kind={messageType} " +
                $"bytes={packetBytes.Length} mode={mode} channel={channel}");
            byte[] payload = packetBytes.AsSpan(HeaderSize, actualLength).ToArray();
            PostToMainThread(() => Dispatch(service, senderId, messageType, payload));
        }
        catch (Exception ex)
        {
            GD.PrintErr(
                $"{RetryMod.LogPrefix}restart raw packet dropped sender={senderId} " +
                $"mode={mode} channel={channel}: {ex.GetType().Name}:{ex.Message}");
        }
        return true;
    }

    private static void Dispatch(
        INetGameService service,
        ulong senderId,
        RestartRawMessageType messageType,
        byte[] payload)
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
                        throw new InvalidOperationException(
                            $"unsupported control protocol {protocol}");
                    var kind = (RestartControlKind)reader.ReadByte();
                    if (!Enum.IsDefined(kind))
                        throw new InvalidOperationException(
                            $"unknown control kind {(byte)kind}");
                    MultiplayerRestartCoordinator.OnRawControl(
                        service,
                        new RestartControlPacket
                        {
                            Protocol = protocol,
                            Kind = kind,
                            Generation = reader.ReadUInt(32),
                            NonceHigh = reader.ReadULong(64),
                            NonceLow = reader.ReadULong(64),
                            HostId = reader.ReadULong(64),
                            ActorId = reader.ReadULong(64),
                        },
                        senderId);
                    break;
                case RestartRawMessageType.DirectSave:
                    if (service is not NetClientGameService client
                        || senderId == 0
                        || senderId != client.HostNetId)
                        throw new InvalidOperationException(
                            $"direct-save sender {senderId} is not the current host");
                    MultiplayerRestartCoordinator.OnRawDirectPayload(
                        service,
                        new DirectRestartPayload
                        {
                            Generation = reader.ReadUInt(32),
                            NonceHigh = reader.ReadULong(64),
                            NonceLow = reader.ReadULong(64),
                            HostId = reader.ReadULong(64),
                            ActorId = reader.ReadULong(64),
                            Run = reader.Read<SerializableRun>(),
                        },
                        senderId);
                    break;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr(
                $"{RetryMod.LogPrefix}restart raw payload rejected sender={senderId} " +
                $"type={messageType}: {ex.GetType().Name}:{ex.Message}");
        }
    }

    private static byte[] FinishPayload(PacketWriter writer)
    {
        writer.ZeroByteRemainder();
        int length = checked((writer.BitPosition + 7) / 8);
        if (length <= 0) throw new InvalidOperationException("restart payload is empty");
        var payload = new byte[length];
        Buffer.BlockCopy(writer.Buffer, 0, payload, 0, length);
        return payload;
    }

    private static byte[] BuildEnvelope(RestartRawMessageType messageType, byte[] payload)
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

    private static bool HasMagic(byte[] packetBytes) =>
        packetBytes.Length >= Magic.Length
        && packetBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    // SteamNetworkingSockets ignores the channel argument when sending and
    // reports m_nChannel as -1 on receive. ENet preserves the requested
    // reliable channel 0, so accept both representations of the same route.
    private static bool IsExpectedReceiveChannel(int channel) =>
        channel is ReliableChannel or SteamUnspecifiedChannel;

    private static void SendRaw(INetGameService service, ulong target, byte[] packet)
    {
        if (target == 0) throw new InvalidOperationException("restart raw target is zero");
        if (!service.IsConnected) throw new InvalidOperationException("network service is disconnected");

        switch (service)
        {
            case NetClientGameService client:
                if (client.HostNetId != target)
                    throw new InvalidOperationException(
                        $"client raw target {target} is not current host {client.HostNetId}");
                var netClient = client.NetClient
                    ?? throw new InvalidOperationException("client transport is unavailable");
                if (netClient is GameSteamClient steamClient)
                {
                    SendSteamRaw(
                        GetSteamClientConnection(steamClient),
                        "client-to-host",
                        service,
                        target,
                        packet);
                }
                else
                {
                    netClient.SendMessageToHost(
                        packet, packet.Length, NetTransferMode.Reliable, ReliableChannel);
                    LogNativeSend(netClient.GetType().Name, "client-to-host", service, target, packet);
                }
                break;
            case NetHostGameService host:
                if (HostTransportField?.GetValue(host) is not NetHost netHost)
                    throw new MissingFieldException(
                        typeof(NetHostGameService).FullName, "_netHost");
                if (RunManager.Instance?.RunLobby?.ConnectedPlayerIds.Contains(target) != true)
                    throw new InvalidOperationException(
                        $"raw target {target} is not in the current run lobby");
                if (!netHost.ConnectedPeerIds.Contains(target))
                    throw new InvalidOperationException($"raw target {target} is not connected");
                if (netHost is GameSteamHost steamHost)
                {
                    SendSteamRaw(
                        GetSteamHostConnection(steamHost, target),
                        "host-to-client",
                        service,
                        target,
                        packet);
                }
                else
                {
                    netHost.SendMessageToClient(
                        target, packet, packet.Length, NetTransferMode.Reliable, ReliableChannel);
                    LogNativeSend(netHost.GetType().Name, "host-to-client", service, target, packet);
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"unsupported network service {service.GetType().FullName}");
        }
    }

    private static HSteamNetConnection GetSteamClientConnection(GameSteamClient client)
    {
        if (SteamClientConnectionField == null)
            throw new MissingFieldException(typeof(GameSteamClient).FullName, "_conn");
        if (SteamClientConnectionField.GetValue(client) is not HSteamNetConnection connection
            || connection.m_HSteamNetConnection == 0)
            throw new InvalidOperationException("Steam client connection is unavailable");
        return connection;
    }

    private static HSteamNetConnection GetSteamHostConnection(GameSteamHost host, ulong target)
    {
        if (SteamHostGetConnectionMethod == null)
            throw new MissingMethodException(typeof(GameSteamHost).FullName, "GetConnectionForNetId");
        object? peer = SteamHostGetConnectionMethod.Invoke(host, [target]);
        if (peer == null)
            throw new InvalidOperationException($"Steam host has no connection for peer {target}");
        if (SteamHostConnectionField == null)
            throw new MissingFieldException("SteamHost.ClientConnection", "conn");
        if (SteamHostConnectionField.GetValue(peer) is not HSteamNetConnection connection
            || connection.m_HSteamNetConnection == 0)
            throw new InvalidOperationException($"Steam connection for peer {target} is invalid");
        return connection;
    }

    private static void SendSteamRaw(
        HSteamNetConnection connection,
        string direction,
        INetGameService service,
        ulong target,
        byte[] packet)
    {
        if (connection.m_HSteamNetConnection == 0)
            throw new InvalidOperationException("Steam connection handle is invalid");

        long messageNumber = -1;
        EResult sendResult = EResult.k_EResultFail;
        EResult flushResult = EResult.k_EResultFail;
        GCHandle pinned = default;
        try
        {
            pinned = GCHandle.Alloc(packet, GCHandleType.Pinned);
            sendResult = SteamNetworkingSockets.SendMessageToConnection(
                connection,
                pinned.AddrOfPinnedObject(),
                checked((uint)packet.Length),
                Steamworks.Constants.k_nSteamNetworkingSend_ReliableNoNagle,
                out messageNumber);
            flushResult = SteamNetworkingSockets.FlushMessagesOnConnection(connection);
        }
        finally
        {
            if (pinned.IsAllocated) pinned.Free();
            GD.Print(
                $"{RetryMod.LogPrefix}restart raw-send transport=Steam direction={direction} " +
                $"localType={service.Type} local={service.NetId} target={target} " +
                $"kind={GetMessageType(packet)} bytes={packet.Length} messageNumber={messageNumber} " +
                $"send={FormatSteamResult(sendResult)} flush={FormatSteamResult(flushResult)}");
        }

        if (sendResult != EResult.k_EResultOK)
            throw new InvalidOperationException(
                $"Steam raw send failed target={target} result={sendResult}");
        if (flushResult != EResult.k_EResultOK)
            throw new InvalidOperationException(
                $"Steam raw flush failed target={target} result={flushResult}");
    }

    private static void LogNativeSend(
        string transport,
        string direction,
        INetGameService service,
        ulong target,
        byte[] packet) =>
        GD.Print(
            $"{RetryMod.LogPrefix}restart raw-send transport={transport} direction={direction} " +
            $"localType={service.Type} local={service.NetId} target={target} " +
            $"kind={GetMessageType(packet)} bytes={packet.Length} channel={ReliableChannel} send=queued");

    private static RestartRawMessageType GetMessageType(byte[] packet) =>
        packet.Length > 17 ? (RestartRawMessageType)packet[17] : 0;

    private static string FormatSteamResult(EResult result) =>
        result == EResult.k_EResultOK ? "OK" : result.ToString();

    private static void PostToMainThread(Action action)
    {
        var game = MegaCrit.Sts2.Core.Nodes.NGame.Instance;
        if (game == null)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}restart raw packet dropped: NGame.Instance is unavailable");
            return;
        }
        Callable.From(action).CallDeferred();
    }
}

[HarmonyPatch(typeof(NetClientGameService), "OnPacketReceived")]
internal static class RestartRawTransport_ClientReceivePatch
{
    private static bool Prefix(
        NetClientGameService __instance,
        ulong senderId,
        byte[] packetBytes,
        NetTransferMode mode,
        int channel) =>
        !RestartRawTransport.TryConsume(__instance, senderId, packetBytes, mode, channel);
}

[HarmonyPatch(typeof(NetHostGameService), "OnPacketReceived")]
internal static class RestartRawTransport_HostReceivePatch
{
    private static bool Prefix(
        NetHostGameService __instance,
        ulong senderId,
        byte[] packetBytes,
        NetTransferMode mode,
        int channel) =>
        !RestartRawTransport.TryConsume(__instance, senderId, packetBytes, mode, channel);
}
