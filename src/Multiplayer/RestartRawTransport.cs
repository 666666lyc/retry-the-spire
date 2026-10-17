using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Saves;

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
    internal SerializableRun Run = null !;
}

/// <summary>
/// A private side-band protocol that bypasses the game's generated message-id
/// table. Packets are sent only after the vanilla initial-info handshake has
/// proved that the other endpoint is running Retry 0.5.0 or newer.
/// </summary>
internal static class RestartRawTransport
{
    internal static void SendControl(INetGameService service, ulong target, RestartControlPacket message) => RestartPayloadSerializer.SendControl(service, target, message);
    internal static void SendDirect(INetGameService service, ulong target, DirectRestartPayload message) => RestartPayloadSerializer.SendDirect(service, target, message);
    /// <summary>Returns true when the packet belongs to Retry and was consumed.</summary>
    internal static bool TryConsume(INetGameService service, ulong senderId, byte[] packetBytes, NetTransferMode mode, int channel)
    {
        if (!RestartEnvelope.HasMagic(packetBytes))
            return false;
        try
        {
            int actualLength = RestartEnvelope.Validate(packetBytes, mode, channel);
            var messageType = (RestartRawMessageType)packetBytes[17];
            GD.Print($"{RetryMod.LogPrefix}restart raw-envelope-received localType={service.Type} " + $"local={service.NetId} sender={senderId} kind={messageType} " + $"bytes={packetBytes.Length} mode={mode} channel={channel}");
            byte[] payload = packetBytes.AsSpan(RestartEnvelope.HeaderSize, actualLength).ToArray();
            RestartMainThread.PostToMainThread(() => RestartPayloadSerializer.Dispatch(service, senderId, messageType, payload));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}restart raw packet dropped sender={senderId} " + $"mode={mode} channel={channel}: {ex.GetType().Name}:{ex.Message}");
        }

        return true;
    }
}

[HarmonyPatch(typeof(NetClientGameService), "OnPacketReceived")]
internal static class RestartRawTransport_ClientReceivePatch
{
    private static bool Prefix(NetClientGameService __instance, ulong senderId, byte[] packetBytes, NetTransferMode mode, int channel) => !RestartRawTransport.TryConsume(__instance, senderId, packetBytes, mode, channel);
}

[HarmonyPatch(typeof(NetHostGameService), "OnPacketReceived")]
internal static class RestartRawTransport_HostReceivePatch
{
    private static bool Prefix(NetHostGameService __instance, ulong senderId, byte[] packetBytes, NetTransferMode mode, int channel) => !RestartRawTransport.TryConsume(__instance, senderId, packetBytes, mode, channel);
}
