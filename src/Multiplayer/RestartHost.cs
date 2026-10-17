using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using static Retry.MultiplayerRestartCoordinator;

namespace Retry;

internal sealed class RestartHost
{
    private readonly RestartSession _session;
    internal RestartHost(RestartSession session) => _session = session;
    internal void BeginHostPreparation(SerializableRun directSave)
    {
        if (_session.Attempt._restartRequested || _session.Attempt.CurrentPhase != Phase.Idle)
            return;
        _session.Capabilities.BindToCurrentService();
        var manager = RunManager.Instance;
        var net = manager?.NetService;
        if (manager?.RunLobby == null || net?.Type != NetGameType.Host)
        {
            QuickRoomRestart.ContinueAfterPreparation();
            return;
        }

        _session.Attempt._restartRequested = true;
        _session.Attempt.CurrentPhase = Phase.Preparing;
        _session.Attempt._generation++;
        if (_session.Attempt._generation == 0)
            _session.Attempt._generation = 1;
        Span<byte> nonce = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        _session.Attempt._nonceHigh = BitConverter.ToUInt64(nonce[..8]);
        _session.Attempt._nonceLow = BitConverter.ToUInt64(nonce[8..]);
        _session.Attempt._hostId = net.NetId;
        _session.Attempt._prepareStarted = Stopwatch.GetTimestamp();
        _session.Attempt.PendingProbe.Clear();
        _session.Attempt.PendingPrepare.Clear();
        _session.Attempt.PreparedPeers.Clear();
        _session.Attempt.PendingDirectPrepare.Clear();
        _session.Attempt.SendRounds.Clear();
        _session.Attempt._directSave = directSave;
        var connected = manager.RunLobby.ConnectedPlayerIds.Where(id => id != net.NetId).ToHashSet();
        var compatible = _session.Connection.CapablePeers.Where(connected.Contains).ToHashSet();
        var undeclared = connected.Where(id => !_session.Connection.CapablePeers.Contains(id)).ToHashSet();
        var directCapable = _session.Connection.DirectCapablePeers.Where(connected.Contains).ToHashSet();
        _session.Attempt._directCandidate = QuickRoomRestart.CanDirectReload && connected.SetEquals(directCapable);
        var lostCapabilities = _session.Connection.EverCapablePeers.Where(connected.Contains).Where(id => !_session.Connection.CapablePeers.Contains(id)).ToArray();
        _session.Log("prepare-roster", $"connected=[{string.Join(',', connected)}] " + $"compatible=[{string.Join(',', compatible)}] " + $"direct=[{string.Join(',', directCapable)}] " + $"directCandidate={_session.Attempt._directCandidate} " + $"undeclared=[{string.Join(',', undeclared)}]");
        if (lostCapabilities.Length > 0)
        {
            _session.Attempt._restartRequested = false;
            _session.Attempt.CurrentPhase = Phase.Idle;
            string players = string.Join("、", lostCapabilities.Select(_session.PlayerLabel));
            GD.PrintErr($"{RetryMod.LogPrefix}restart capability state lost players={string.Join(',', lostCapabilities)}");
            OneButtonNotice.Show("重打通信异常", $"以下成员此前已确认支持自动重连，但当前能力状态丢失：\n{players}\n\n本次重打已取消，请重新尝试。", "知道了", QuickRoomRestart.CancelPreparation);
            return;
        }

        _session.Attempt.PendingProbe.UnionWith(compatible);
        _session.Attempt.PendingPrepare.UnionWith(_session.Attempt.PendingProbe);
        _session.Attempt.PreparedPeers.UnionWith(compatible);
        _session.Log("prepare-start", $"compatible={_session.Attempt.PendingPrepare.Count} connected={connected.Count} undeclared={undeclared.Count}");
        _ = PrepareAsync(net);
    }

    internal async Task PrepareAsync(INetGameService net)
    {
        // A separate probe/ACK round is the communication verification gate.
        bool probeOk = await RunAckPhase(net, _session.Attempt.PendingProbe, RestartControlKind.Probe, RestartControlKind.ProbeAck);
        if (!probeOk)
        {
            if (_session.Attempt._directCandidate)
            {
                // Capability packets proved these are new clients, but a
                // missed probe must not prevent responsive peers from arming
                // the existing auto-rejoin fallback.
                _session.Attempt.PendingPrepare.Clear();
                _session.Attempt.PendingPrepare.UnionWith(_session.Attempt.PreparedPeers);
                await RunAckPhase(net, _session.Attempt.PendingPrepare, RestartControlKind.Prepare, RestartControlKind.PrepareAck);
                await FallBackToLobbyAsync(net, "legacy-probe-timeout");
                return;
            }

            ShowTimeout("通信探测", _session.Attempt.PendingProbe);
            return;
        }

        bool prepareOk = await RunAckPhase(net, _session.Attempt.PendingPrepare, RestartControlKind.Prepare, RestartControlKind.PrepareAck);
        if (!prepareOk)
        {
            if (_session.Attempt._directCandidate)
            {
                await FallBackToLobbyAsync(net, "legacy-prepare-timeout");
                return;
            }

            ShowTimeout("重打通知", _session.Attempt.PendingPrepare);
            return;
        }

        if (_session.Attempt._directCandidate)
        {
            bool directOk = await PrepareDirectRestartAsync(net);
            if (!directOk)
            {
                await FallBackToLobbyAsync(net, "direct-prepare-timeout");
                return;
            }

            _session.Attempt.CurrentPhase = Phase.AllAcknowledged;
            _session.Log("direct-all-acknowledged", $"elapsedMs={_session.ElapsedMs(_session.Attempt._prepareStarted):F0}");
            await CommitDirectRestartAsync(net);
            return;
        }

        _session.Attempt.CurrentPhase = Phase.AllAcknowledged;
        _session.Log("all-acknowledged", $"elapsedMs={_session.ElapsedMs(_session.Attempt._prepareStarted):F0}");
        await CommitAndContinueAsync(net);
    }

    internal async Task<bool> PrepareDirectRestartAsync(INetGameService net)
    {
        var save = _session.Attempt._directSave;
        if (save == null)
            return false;
        _session.Attempt.CurrentPhase = Phase.DirectPreparing;
        _session.Attempt.PendingDirectPrepare.Clear();
        _session.Attempt.PendingDirectPrepare.UnionWith(_session.Connection.DirectCapablePeers.Where(_session.Attempt.PreparedPeers.Contains));
        long started = Stopwatch.GetTimestamp();
        int round = 0;
        while (_session.Attempt.PendingDirectPrepare.Count > 0 && Stopwatch.GetElapsedTime(started).TotalSeconds < RestartSession.PrepareTimeoutSeconds)
        {
            round++;
            foreach (ulong peer in _session.Attempt.PendingDirectPrepare.ToArray())
            {
                try
                {
                    RestartRawTransport.SendDirect(net, peer, new DirectRestartPayload { Generation = _session.Attempt._generation, NonceHigh = _session.Attempt._nonceHigh, NonceLow = _session.Attempt._nonceLow, HostId = _session.Attempt._hostId, ActorId = net.NetId, Run = save, });
                    _session.Log("direct-save-send", $"target={peer} round={round}");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}restart direct save target={peer} " + $"round={round}: {ex.Message}");
                }
            }

            await _session.DelayAsync(RestartSession.DirectResendSeconds);
            if (_session.Attempt.CurrentPhase != Phase.DirectPreparing)
                return false;
        }

        _session.Log("direct-prepare-phase", $"remaining={string.Join(',', _session.Attempt.PendingDirectPrepare)} elapsedMs={_session.ElapsedMs(started):F0}");
        return _session.Attempt.PendingDirectPrepare.Count == 0;
    }

    internal async Task CommitDirectRestartAsync(INetGameService net)
    {
        for (int round = 1; round <= 2; round++)
        {
            foreach (ulong peer in _session.Attempt.PreparedPeers.Where(_session.Connection.DirectCapablePeers.Contains).ToArray())
            {
                try
                {
                    _session.Send(net, peer, RestartControlKind.DirectCommit, _session.Attempt._generation, _session.Attempt._nonceHigh, _session.Attempt._nonceLow, _session.Attempt._hostId);
                    _session.Log("direct-commit-send", $"target={peer} round={round}");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}restart direct commit target={peer} " + $"round={round}: {ex.Message}");
                }
            }

            if (round == 1 && _session.Attempt.PreparedPeers.Count > 0)
                await _session.DelayAsync(0.05);
        }

        _session.Attempt._restartRequested = false;
        _session.Attempt._directSave = null;
        _session.Attempt.CurrentPhase = Phase.DirectLoading;
        _session.Log("direct-commit-complete", $"targets={_session.Attempt.PreparedPeers.Count}");
        QuickRoomRestart.ContinueDirectAfterPreparation();
    }

    internal async Task FallBackToLobbyAsync(INetGameService net, string reason)
    {
        _session.Log("direct-fallback", $"reason={reason}");
        _session.Attempt.PendingProbe.Clear();
        _session.Attempt.PendingPrepare.Clear();
        _session.Attempt.PendingDirectPrepare.Clear();
        _session.Attempt._directCandidate = false;
        foreach (ulong peer in _session.Attempt.PreparedPeers.Where(_session.Connection.DirectCapablePeers.Contains).ToArray())
        {
            try
            {
                _session.Send(net, peer, RestartControlKind.UseLobbyFallback, _session.Attempt._generation, _session.Attempt._nonceHigh, _session.Attempt._nonceLow, _session.Attempt._hostId);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}restart direct fallback target={peer}: {ex.Message}");
            }
        }

        _session.Attempt.CurrentPhase = Phase.AllAcknowledged;
        await CommitAndContinueAsync(net);
    }

    internal async Task<bool> RunAckPhase(INetGameService net, HashSet<ulong> pending, RestartControlKind outgoing, RestartControlKind expectedAck)
    {
        if (pending.Count == 0)
            return true;
        long started = Stopwatch.GetTimestamp();
        while (pending.Count > 0 && Stopwatch.GetElapsedTime(started).TotalSeconds < RestartSession.PrepareTimeoutSeconds)
        {
            foreach (ulong peer in pending.ToArray())
            {
                _session.Attempt.SendRounds.TryGetValue(peer, out int round);
                _session.Attempt.SendRounds[peer] = round + 1;
                try
                {
                    _session.Send(net, peer, outgoing, _session.Attempt._generation, _session.Attempt._nonceHigh, _session.Attempt._nonceLow, _session.Attempt._hostId);
                    _session.Log("send", $"kind={outgoing} target={peer} round={round + 1}");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}restart send kind={outgoing} " + $"target={peer} round={round + 1}: {ex.Message}");
                }
            }

            if (pending.Count == 0)
                break;
            double remaining = RestartSession.PrepareTimeoutSeconds - Stopwatch.GetElapsedTime(started).TotalSeconds;
            if (remaining <= 0)
                break;
            await _session.DelayAsync(Math.Min(RestartSession.ResendSeconds, remaining));
            // ACK handlers remove peers only for the currently active phase/kind.
            if (outgoing == RestartControlKind.Probe && _session.Attempt.CurrentPhase != Phase.Preparing)
                return false;
        }

        _session.Log("ack-phase", $"kind={expectedAck} remaining={string.Join(',', pending)} elapsedMs={_session.ElapsedMs(started):F0}");
        return pending.Count == 0;
    }

    internal void ShowTimeout(string stage, HashSet<ulong> pending)
    {
        string ids = pending.Count == 0 ? "未知" : string.Join("、", pending.Select(_session.PlayerLabel));
        _session.Log("timeout", $"stage={stage} pending={string.Join(',', pending)}");
        MultiplayerRestartPrompt.Show($"{stage}未完成，以下成员没有响应：\n{ids}\n\n当前对局仍保持连接。取消后可以再次尝试；只有选择“仍然重开”才会让这些成员改为手动加入。", force =>
        {
            _session.Attempt.PendingProbe.Clear();
            _session.Attempt.PendingPrepare.Clear();
            _session.Attempt._restartRequested = false;
            if (!force)
            {
                BroadcastCancel();
                _session.ResetAttempt();
                QuickRoomRestart.CancelPreparation();
                return;
            }

            _session.Attempt.CurrentPhase = Phase.AllAcknowledged;
            var net = RunManager.Instance?.NetService;
            if (net?.Type == NetGameType.Host)
                _ = CommitAndContinueAsync(net);
            else
            {
                _session.Attempt._restartRequested = false;
                QuickRoomRestart.ContinueAfterPreparation();
            }
        });
    }

    internal async Task CommitAndContinueAsync(INetGameService net)
    {
        // Commit lets prepared v0.5.0+ clients begin the native main-menu
        // transition before the old Steam connection closes.
        ulong[] peers = _session.Attempt.PreparedPeers.ToArray();
        foreach (ulong peer in peers)
        {
            try
            {
                _session.Send(net, peer, RestartControlKind.CommitRestart, _session.Attempt._generation, _session.Attempt._nonceHigh, _session.Attempt._nonceLow, _session.Attempt._hostId);
                _session.Log("commit-send", $"target={peer} round=1");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}restart commit target={peer} round=1: {ex.Message}");
            }
        }

        for (int round = 2; round <= 2; round++)
        {
            if (peers.Length > 0)
                await _session.DelayAsync(0.05);
            foreach (ulong peer in peers)
            {
                try
                {
                    _session.Send(net, peer, RestartControlKind.CommitRestart, _session.Attempt._generation, _session.Attempt._nonceHigh, _session.Attempt._nonceLow, _session.Attempt._hostId);
                    _session.Log("commit-send", $"target={peer} round={round}");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}restart commit target={peer} round={round}: {ex.Message}");
                }
            }
        }

        _session.Attempt._restartRequested = false;
        _session.Attempt._directCandidate = false;
        _session.Attempt._directSave = null;
        _session.Log("commit-complete", $"targets={_session.Attempt.PreparedPeers.Count} elapsedMs={_session.ElapsedMs(_session.Attempt._prepareStarted):F0}");
        QuickRoomRestart.ContinueAfterPreparation();
    }

    internal void AbortBeforeDisconnect()
    {
        BroadcastCancel();
        _session.Attempt._restartRequested = false;
        _session.Attempt._hostAutoStartArmed = false;
        _session.Attempt._hostReadySent = false;
        _session.ResetAttempt();
    }

    internal void BroadcastCancel()
    {
        var manager = RunManager.Instance;
        var net = manager?.NetService;
        if (net?.Type != NetGameType.Host || manager?.RunLobby == null || _session.Attempt._hostId == 0)
            return;
        foreach (ulong peer in _session.Connection.CapablePeers.Where(manager.RunLobby.ConnectedPlayerIds.Contains).ToArray())
        {
            try
            {
                _session.Send(net, peer, RestartControlKind.Cancel, _session.Attempt._generation, _session.Attempt._nonceHigh, _session.Attempt._nonceLow, _session.Attempt._hostId);
                _session.Log("cancel-send", $"target={peer}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}restart cancel target={peer}: {ex.Message}");
            }
        }
    }
}
