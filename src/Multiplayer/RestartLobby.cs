using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using static Retry.MultiplayerRestartCoordinator;

namespace Retry;

internal sealed class RestartLobby
{
    private readonly RestartSession _session;
    internal RestartLobby(RestartSession session) => _session = session;
    internal void AutoReadyClient(LoadRunLobby lobby)
    {
        var session = _session.Rejoin._clientSession;
        if (session == null || lobby.NetService.Type != NetGameType.Client)
            return;
        if (lobby.NetService is not NetClientGameService client || client.HostNetId != session.HostId)
            return;
        ulong me = lobby.NetService.NetId;
        _session.LogSession(session, "lobby-entered", $"local={me} lobby={_session.SafeLobbyIdentifier(lobby.NetService) ?? "?"}");
        if (lobby.Run.Players.All(p => p.NetId != me))
        {
            session.Outcome = AutoJoinOutcome.RejectedNotInSave;
            session.OutcomeDetail = $"local={me} absent-from-save";
            _session.Rejoin._suppressExpectedDisconnect = false;
            _session.LogSession(session, "auto-ready-rejected", session.OutcomeDetail);
            return;
        }

        try
        {
            if (!lobby.IsPlayerReady(me))
                lobby.SetReady(true);
            session.Outcome = AutoJoinOutcome.Ready;
            _session.Rejoin._suppressExpectedDisconnect = false;
            _session.LogSession(session, "auto-ready", $"local={me}");
        }
        catch (Exception ex)
        {
            session.Outcome = AutoJoinOutcome.JoinedNeedsManualReady;
            session.OutcomeDetail = ex.Message;
            _session.Rejoin._suppressExpectedDisconnect = false;
            GD.PrintErr($"{RetryMod.LogPrefix}restart auto-ready local={me}: {ex.Message}");
            _session.LogSession(session, "auto-ready-failed", $"local={me} error={ex.GetType().Name}:{ex.Message}");
        }
    }

    internal void ArmHostAutoStart()
    {
        _session.Attempt.CurrentPhase = Phase.ReopeningLobby;
        _session.Attempt._hostAutoStartArmed = true;
        _session.Attempt._hostReadySent = false;
    }

    internal void TryAutoStartHost(LoadRunLobby lobby)
    {
        if (!_session.Attempt._hostAutoStartArmed || _session.Attempt._hostReadySent || lobby.NetService.Type != NetGameType.Host)
            return;
        _session.Attempt.CurrentPhase = Phase.WaitingForPlayers;
        ulong host = lobby.NetService.NetId;
        ulong[] expected = lobby.Run.Players.Select(p => p.NetId).Distinct().ToArray();
        if (expected.Length == 0 || expected.Any(id => !lobby.ConnectedPlayerIds.Contains(id)))
            return;
        if (expected.Where(id => id != host).Any(id => !lobby.IsPlayerReady(id)))
            return;
        _session.Attempt._hostReadySent = true;
        _session.Attempt._hostAutoStartArmed = false;
        _session.Attempt.CurrentPhase = Phase.Starting;
        try
        {
            if (!lobby.IsPlayerReady(host))
                lobby.SetReady(true);
            _session.Log("host-auto-ready", $"players={expected.Length}");
        }
        catch (Exception ex)
        {
            _session.Attempt._hostReadySent = false;
            GD.PrintErr($"{RetryMod.LogPrefix}restart host auto-ready: {ex.Message}");
        }
    }
}
