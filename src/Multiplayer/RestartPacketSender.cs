using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;
using Steamworks;
using GameSteamClient = MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamClient;
using GameSteamHost = MegaCrit.Sts2.Core.Multiplayer.Transport.Steam.SteamHost;

namespace Retry;

internal static class RestartPacketSender
{
    internal static readonly FieldInfo? HostTransportField = typeof(NetHostGameService).GetField("_netHost", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static readonly FieldInfo? SteamClientConnectionField = typeof(GameSteamClient).GetField("_conn", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static readonly MethodInfo? SteamHostGetConnectionMethod = typeof(GameSteamHost).GetMethod("GetConnectionForNetId", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static readonly FieldInfo? SteamHostConnectionField = typeof(GameSteamHost).GetNestedType("ClientConnection", BindingFlags.NonPublic)?.GetField("conn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    internal static void SendRaw(INetGameService service, ulong target, byte[] packet)
    {
        if (target == 0)
            throw new InvalidOperationException("restart raw target is zero");
        if (!service.IsConnected)
            throw new InvalidOperationException("network service is disconnected");
        switch (service)
        {
            case NetClientGameService client:
                if (client.HostNetId != target)
                    throw new InvalidOperationException($"client raw target {target} is not current host {client.HostNetId}");
                var netClient = client.NetClient ?? throw new InvalidOperationException("client transport is unavailable");
                if (netClient is GameSteamClient steamClient)
                {
                    SendSteamRaw(GetSteamClientConnection(steamClient), "client-to-host", service, target, packet);
                }
                else
                {
                    netClient.SendMessageToHost(packet, packet.Length, NetTransferMode.Reliable, RestartEnvelope.ReliableChannel);
                    LogNativeSend(netClient.GetType().Name, "client-to-host", service, target, packet);
                }

                break;
            case NetHostGameService host:
                if (HostTransportField?.GetValue(host)is not NetHost netHost)
                    throw new MissingFieldException(typeof(NetHostGameService).FullName, "_netHost");
                if (RunManager.Instance?.RunLobby?.ConnectedPlayerIds.Contains(target) != true)
                    throw new InvalidOperationException($"raw target {target} is not in the current run lobby");
                if (!netHost.ConnectedPeerIds.Contains(target))
                    throw new InvalidOperationException($"raw target {target} is not connected");
                if (netHost is GameSteamHost steamHost)
                {
                    SendSteamRaw(GetSteamHostConnection(steamHost, target), "host-to-client", service, target, packet);
                }
                else
                {
                    netHost.SendMessageToClient(target, packet, packet.Length, NetTransferMode.Reliable, RestartEnvelope.ReliableChannel);
                    LogNativeSend(netHost.GetType().Name, "host-to-client", service, target, packet);
                }

                break;
            default:
                throw new InvalidOperationException($"unsupported network service {service.GetType().FullName}");
        }
    }

    internal static HSteamNetConnection GetSteamClientConnection(GameSteamClient client)
    {
        if (SteamClientConnectionField == null)
            throw new MissingFieldException(typeof(GameSteamClient).FullName, "_conn");
        if (SteamClientConnectionField.GetValue(client)is not HSteamNetConnection connection || connection.m_HSteamNetConnection == 0)
            throw new InvalidOperationException("Steam client connection is unavailable");
        return connection;
    }

    internal static HSteamNetConnection GetSteamHostConnection(GameSteamHost host, ulong target)
    {
        if (SteamHostGetConnectionMethod == null)
            throw new MissingMethodException(typeof(GameSteamHost).FullName, "GetConnectionForNetId");
        object? peer = SteamHostGetConnectionMethod.Invoke(host, [target]);
        if (peer == null)
            throw new InvalidOperationException($"Steam host has no connection for peer {target}");
        if (SteamHostConnectionField == null)
            throw new MissingFieldException("SteamHost.ClientConnection", "conn");
        if (SteamHostConnectionField.GetValue(peer)is not HSteamNetConnection connection || connection.m_HSteamNetConnection == 0)
            throw new InvalidOperationException($"Steam connection for peer {target} is invalid");
        return connection;
    }

    internal static void SendSteamRaw(HSteamNetConnection connection, string direction, INetGameService service, ulong target, byte[] packet)
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
            sendResult = SteamNetworkingSockets.SendMessageToConnection(connection, pinned.AddrOfPinnedObject(), checked((uint)packet.Length), Steamworks.Constants.k_nSteamNetworkingSend_ReliableNoNagle, out messageNumber);
            flushResult = SteamNetworkingSockets.FlushMessagesOnConnection(connection);
        }
        finally
        {
            if (pinned.IsAllocated)
                pinned.Free();
            GD.Print($"{RetryMod.LogPrefix}restart raw-send transport=Steam direction={direction} " + $"localType={service.Type} local={service.NetId} target={target} " + $"kind={GetMessageType(packet)} bytes={packet.Length} messageNumber={messageNumber} " + $"send={FormatSteamResult(sendResult)} flush={FormatSteamResult(flushResult)}");
        }

        if (sendResult != EResult.k_EResultOK)
            throw new InvalidOperationException($"Steam raw send failed target={target} result={sendResult}");
        if (flushResult != EResult.k_EResultOK)
            throw new InvalidOperationException($"Steam raw flush failed target={target} result={flushResult}");
    }

    internal static void LogNativeSend(string transport, string direction, INetGameService service, ulong target, byte[] packet) => GD.Print($"{RetryMod.LogPrefix}restart raw-send transport={transport} direction={direction} " + $"localType={service.Type} local={service.NetId} target={target} " + $"kind={GetMessageType(packet)} bytes={packet.Length} channel={RestartEnvelope.ReliableChannel} send=queued");
    internal static RestartRawMessageType GetMessageType(byte[] packet) => packet.Length > 17 ? (RestartRawMessageType)packet[17] : 0;
    internal static string FormatSteamResult(EResult result) => result == EResult.k_EResultOK ? "OK" : result.ToString();
}
