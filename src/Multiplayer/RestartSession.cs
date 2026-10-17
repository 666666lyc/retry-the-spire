using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using static Retry.MultiplayerRestartCoordinator;

namespace Retry;

internal sealed class RestartSession
{
    internal readonly RestartConnectionState Connection = new();
    internal readonly RestartAttemptState Attempt = new();
    internal readonly RestartRejoinState Rejoin = new();
    internal RestartCapabilities Capabilities { get; }
    internal RestartHost Host { get; }
    internal RestartClient Client { get; }
    internal RestartLobby Lobby { get; }

    internal RestartSession()
    {
        Capabilities = new RestartCapabilities(this);
        Host = new RestartHost(this);
        Client = new RestartClient(this);
        Lobby = new RestartLobby(this);
    }

    internal const string CompatibleModPrefix = "RetryTheSpire-";
    internal const double PrepareTimeoutSeconds = 3.0;
    internal const double ResendSeconds = 0.15;
    internal const double DirectResendSeconds = 0.5;
    internal const double AutoJoinTimeoutSeconds = 15.0;
    internal void OnRawControl(INetGameService net, RestartControlPacket message, ulong senderId)
    {
        try
        {
            if (!ReferenceEquals(RunManager.Instance?.NetService, net))
            {
                LogReceiveDrop("Control", senderId, "stale-network-service");
                return;
            }

            if (message.Protocol != RestartControlPacket.CurrentProtocol)
            {
                LogReceiveDrop("Control", senderId, $"unsupported-protocol-{message.Protocol}");
                return;
            }

            if (message.ActorId != senderId || senderId == 0)
            {
                LogReceiveDrop("Control", senderId, $"actor-mismatch-actor-{message.ActorId}");
                return;
            }

            Log("receive", $"kind={message.Kind} sender={senderId} actor={message.ActorId} generation={message.Generation}");
            if (message.Kind is RestartControlKind.Capability or RestartControlKind.DirectCapability)
            {
                if (net.Type != NetGameType.Host)
                {
                    LogReceiveDrop("Control", senderId, $"capability-wrong-role-{net.Type}");
                    return;
                }

                if (message.HostId != net.NetId)
                {
                    LogReceiveDrop("Control", senderId, $"capability-host-mismatch-message-{message.HostId}-local-{net.NetId}");
                    return;
                }

                if (RunManager.Instance?.RunLobby?.ConnectedPlayerIds.Contains(senderId) != true)
                {
                    LogReceiveDrop("Control", senderId, "capability-sender-not-in-run-lobby");
                    return;
                }

                bool first = Connection.CapablePeers.Add(senderId);
                Connection.EverCapablePeers.Add(senderId);
                if (message.Kind == RestartControlKind.DirectCapability)
                    Connection.DirectCapablePeers.Add(senderId);
                if (first)
                    Log("capability-received", $"sender={senderId} connected=true");
                if (message.Kind == RestartControlKind.DirectCapability)
                    Log("direct-capability-received", $"sender={senderId} connected=true");
                return;
            }

            if (net.Type == NetGameType.Host)
            {
                if (message.HostId != net.NetId || message.Generation != Attempt._generation || message.NonceHigh != Attempt._nonceHigh || message.NonceLow != Attempt._nonceLow)
                {
                    LogReceiveDrop("Control", senderId, $"host-session-mismatch-messageHost-{message.HostId}-generation-{message.Generation}");
                    return;
                }

                if (message.Kind == RestartControlKind.ProbeAck && Attempt.PendingProbe.Remove(senderId))
                {
                    Log("ack", $"kind=ProbeAck sender={senderId} remaining={Attempt.PendingProbe.Count}");
                }

                if (message.Kind == RestartControlKind.PrepareAck && Attempt.PendingPrepare.Remove(senderId))
                {
                    Log("ack", $"kind=PrepareAck sender={senderId} remaining={Attempt.PendingPrepare.Count}");
                }

                if (message.Kind == RestartControlKind.DirectPrepareAck && Attempt.CurrentPhase == Phase.DirectPreparing && Attempt.PendingDirectPrepare.Remove(senderId))
                {
                    Log("ack", $"kind=DirectPrepareAck sender={senderId} remaining={Attempt.PendingDirectPrepare.Count}");
                }

                return;
            }

            if (net.Type != NetGameType.Client || net is not NetClientGameService client)
            {
                LogReceiveDrop("Control", senderId, $"unsupported-local-role-{net.Type}");
                return;
            }

            if (senderId != client.HostNetId || message.HostId != senderId)
            {
                LogReceiveDrop("Control", senderId, $"client-host-mismatch-current-{client.HostNetId}-message-{message.HostId}");
                return;
            }

            if (message.Kind == RestartControlKind.Probe)
            {
                Send(net, senderId, RestartControlKind.ProbeAck, message.Generation, message.NonceHigh, message.NonceLow, senderId);
                return;
            }

            if (message.Kind == RestartControlKind.Cancel)
            {
                var session = Rejoin._clientSession;
                if (session != null && message.Generation == session.Generation && message.NonceHigh == session.NonceHigh && message.NonceLow == session.NonceLow)
                {
                    Rejoin._suppressExpectedDisconnect = false;
                    LogSession(session, "client-cancelled", "credentials-cleared");
                    Client.ClearClientIdentity(session);
                }
                else
                {
                    LogReceiveDrop("Control", senderId, "cancel-session-mismatch");
                }

                return;
            }

            if (message.Kind == RestartControlKind.CommitRestart)
            {
                var session = Rejoin._clientSession;
                if (session == null || session.HostId != senderId || session.Generation != message.Generation || session.NonceHigh != message.NonceHigh || session.NonceLow != message.NonceLow)
                {
                    LogReceiveDrop("Control", senderId, "commit-session-mismatch");
                    return;
                }

                if (session.Decision == RestartDecision.Direct)
                    return;
                if (!session.Committed)
                {
                    session.Decision = RestartDecision.Lobby;
                    session.DirectSave = null;
                    session.DirectState = null;
                    session.Committed = true;
                    LogSession(session, "client-commit", "begin-native-main-menu-transition");
                }

                Client.BeginCommittedClientExit(session);
                return;
            }

            if (message.Kind == RestartControlKind.UseLobbyFallback)
            {
                var session = Rejoin._clientSession;
                if (SessionMatches(session, senderId, message.Generation, message.NonceHigh, message.NonceLow) && !session!.Committed)
                {
                    session.Decision = RestartDecision.Lobby;
                    session.DirectSave = null;
                    session.DirectState = null;
                    LogSession(session, "direct-fallback-received", "awaiting-lobby-commit");
                }
                else
                {
                    LogReceiveDrop("Control", senderId, "fallback-session-mismatch-or-committed");
                }

                return;
            }

            if (message.Kind == RestartControlKind.DirectCommit)
            {
                var session = Rejoin._clientSession;
                if (SessionMatches(session, senderId, message.Generation, message.NonceHigh, message.NonceLow) && session!.Committed && session.Decision == RestartDecision.Direct)
                    return;
                if (!SessionMatches(session, senderId, message.Generation, message.NonceHigh, message.NonceLow) || session!.Committed || session.Decision == RestartDecision.Lobby || session.DirectSave == null || session.DirectState == null)
                {
                    LogReceiveDrop("Control", senderId, "direct-commit-session-not-ready");
                    return;
                }

                session.Committed = true;
                session.Decision = RestartDecision.Direct;
                Rejoin._autoRejoinArmed = false;
                Rejoin._suppressExpectedDisconnect = false;
                LogSession(session, "direct-commit-received", "begin-in-place-load");
                QuickRoomRestart.BeginDirectClientRestart(session.DirectSave, session.DirectState);
                return;
            }

            if (message.Kind != RestartControlKind.Prepare)
                return;
            // Arm before ACK. Even if the old lobby closes immediately after the
            // ACK reaches the host, the disconnect path can now safely rejoin.
            var existing = Rejoin._clientSession;
            bool sameSession = existing != null && existing.HostId == senderId && existing.Generation == message.Generation && existing.NonceHigh == message.NonceHigh && existing.NonceLow == message.NonceLow;
            var activeSession = sameSession ? existing! : new AutoRejoinSession(senderId, message.Generation, message.NonceHigh, message.NonceLow, SafeLobbyIdentifier(net));
            Rejoin._clientSession = activeSession;
            Attempt._generation = message.Generation;
            Attempt._nonceHigh = message.NonceHigh;
            Attempt._nonceLow = message.NonceLow;
            Attempt._hostId = senderId;
            Rejoin._autoRejoinArmed = true;
            Rejoin._suppressExpectedDisconnect = true;
            Rejoin._oldLobbyIdentifier = activeSession.OldLobbyIdentifier;
            Send(net, senderId, RestartControlKind.PrepareAck, message.Generation, message.NonceHigh, message.NonceLow, senderId);
            LogSession(activeSession, sameSession ? "client-prepare-duplicate" : "client-armed", $"oldLobby={activeSession.OldLobbyIdentifier ?? "?"}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}restart control receive: {ex.Message}");
        }
    }

    internal void OnRawDirectPayload(INetGameService net, DirectRestartPayload message, ulong senderId)
    {
        try
        {
            if (!ReferenceEquals(RunManager.Instance?.NetService, net))
            {
                LogReceiveDrop("DirectSave", senderId, "stale-network-service");
                return;
            }

            if (net is not NetClientGameService client)
            {
                LogReceiveDrop("DirectSave", senderId, $"unsupported-local-role-{net.Type}");
                return;
            }

            if (senderId == 0 || senderId != client.HostNetId || message.ActorId != senderId || message.HostId != senderId)
            {
                LogReceiveDrop("DirectSave", senderId, $"identity-mismatch-currentHost-{client.HostNetId}-messageHost-{message.HostId}-actor-{message.ActorId}");
                return;
            }

            var session = Rejoin._clientSession;
            if (!SessionMatches(session, senderId, message.Generation, message.NonceHigh, message.NonceLow) || session!.Committed || session.Decision == RestartDecision.Lobby)
            {
                LogReceiveDrop("DirectSave", senderId, "session-mismatch-committed-or-lobby");
                return;
            }

            if (session.DirectSave == null || session.DirectState == null)
            {
                ulong localId = net.NetId;
                if (message.Run.Players.All(p => p.NetId != localId))
                    throw new InvalidOperationException($"local player {localId} is absent from direct restart save");
                session.DirectState = RunState.FromSerializable(message.Run);
                session.DirectSave = message.Run;
                LogSession(session, "direct-save-validated", $"local={localId}");
            }

            Send(net, senderId, RestartControlKind.DirectPrepareAck, message.Generation, message.NonceHigh, message.NonceLow, senderId);
        }
        catch (Exception ex)
        {
            // Withhold the ACK. The host will automatically choose the native
            // lobby path after the bounded direct-prepare timeout.
            GD.PrintErr($"{RetryMod.LogPrefix}restart direct save rejected: {ex.Message}");
        }
    }

    internal bool SessionMatches(AutoRejoinSession? session, ulong hostId, uint generation, ulong nonceHigh, ulong nonceLow) => session != null && session.HostId == hostId && session.Generation == generation && session.NonceHigh == nonceHigh && session.NonceLow == nonceLow;
    internal void ResetAfterRunLaunch()
    {
        if (Attempt.CurrentPhase is Phase.Starting or Phase.DirectLoading)
        {
            Capabilities.ClearPeerCapabilities();
            Attempt.CurrentPhase = Phase.Idle;
        }

        Capabilities.BindToCurrentService();
        var net = RunManager.Instance?.NetService;
        if (Connection._hostSupportsProtocol && net is NetClientGameService client && client.HostNetId != 0)
        {
            GD.Print($"{RetryMod.LogPrefix}restart event=capability-refresh " + $"local={net.NetId} host={client.HostNetId} trigger=run-launch");
            _ = Capabilities.AnnounceCapabilityAsync(net, client.HostNetId);
        }
    }

    internal void DirectLoadCompleted()
    {
        var session = Rejoin._clientSession;
        if (session?.Decision == RestartDecision.Direct)
            Client.ClearClientIdentity(session);
        ResetAttempt();
        Log("direct-load-complete", "session-cleared");
    }

    internal void DirectLoadFailed()
    {
        var session = Rejoin._clientSession;
        if (session?.Decision == RestartDecision.Direct)
            Client.ClearClientIdentity(session);
        Capabilities.ClearPeerCapabilities();
        ResetAttempt();
    }

    internal void Send(INetGameService net, ulong target, RestartControlKind kind, uint generation, ulong nonceHigh, ulong nonceLow, ulong hostId)
    {
        if (target == 0)
            throw new InvalidOperationException("restart message target is zero");
        RestartRawTransport.SendControl(net, target, new RestartControlPacket { Kind = kind, Generation = generation, NonceHigh = nonceHigh, NonceLow = nonceLow, HostId = hostId, ActorId = net.NetId, });
    }

    internal string PlayerLabel(ulong id)
    {
        try
        {
            return $"{PlatformUtil.GetPlayerName(PlatformUtil.PrimaryPlatform, id)} ({id})";
        }
        catch
        {
            return id.ToString();
        }
    }

    internal string? SafeLobbyIdentifier(INetGameService net)
    {
        try
        {
            return net.GetRawLobbyIdentifier();
        }
        catch
        {
            return null;
        }
    }

    internal async Task DelayAsync(double seconds)
    {
        var game = NGame.Instance;
        if (game == null)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            return;
        }

        await game.ToSignal(game.GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    internal void ResetAttempt()
    {
        Attempt.CurrentPhase = Phase.Idle;
        Attempt.PendingProbe.Clear();
        Attempt.PendingPrepare.Clear();
        Attempt.PendingDirectPrepare.Clear();
        Attempt.PreparedPeers.Clear();
        Attempt.SendRounds.Clear();
        Attempt._restartRequested = false;
        Attempt._directCandidate = false;
        Attempt._directSave = null;
        Attempt._nonceHigh = Attempt._nonceLow = 0;
        Attempt._hostId = 0;
        Attempt._prepareStarted = 0;
    }

    internal double ElapsedMs(long started) => started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    internal void Log(string evt, string detail) => GD.Print($"{RetryMod.LogPrefix}restart event={evt} phase={Attempt.CurrentPhase} generation={Attempt._generation} " + $"nonce={Attempt._nonceHigh:x16}{Attempt._nonceLow:x16} host={Attempt._hostId} {detail}");
    internal void LogReceiveDrop(string packet, ulong senderId, string reason) => GD.Print($"{RetryMod.LogPrefix}restart event=receive-drop packet={packet} sender={senderId} " + $"reason={reason}");
    internal void LogSession(AutoRejoinSession session, string evt, string detail) => GD.Print($"{RetryMod.LogPrefix}restart event={evt} phase={Attempt.CurrentPhase} generation={session.Generation} " + $"nonce={session.NonceHigh:x16}{session.NonceLow:x16} host={session.HostId} " + $"elapsedMs={ElapsedMs(session.Started):F0} {detail}");
}
