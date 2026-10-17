using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using static Retry.MultiplayerRestartCoordinator;

namespace Retry;

internal sealed class RestartClient
{
    private readonly RestartSession _session;
    internal RestartClient(RestartSession session) => _session = session;
    internal void OnClientDisconnected(ulong hostId, NetErrorInfo info)
    {
        var session = _session.Rejoin._clientSession;
        _session.Capabilities.ClearNetworkBinding();
        if (session == null || hostId != session.HostId)
            return;
        if (info.SelfInitiated && !session.Committed)
            return;
        if (info.GetReason()is not (NetError.Quit or NetError.HostAbandoned))
            return;
        _session.LogSession(session, "old-lobby-disconnected", $"reason={info.GetReason()} selfInitiated={info.SelfInitiated}");
    }

    internal void OnMainMenuReady(NMainMenu menu)
    {
        var session = _session.Rejoin._clientSession;
        _session.Capabilities.ClearNetworkBinding();
        if (session == null || !_session.Rejoin._autoRejoinArmed || session.HostId == 0 || session.SearchStarted)
            return;
        session.SearchStarted = true;
        _session.Rejoin._autoRejoinArmed = false;
        _session.LogSession(session, "main-menu-ready", "auto-search-starting");
        _ = AutoRejoinAsync(menu, session);
    }

    internal async Task AutoRejoinAsync(NMainMenu menu, AutoRejoinSession session)
    {
        long started = Stopwatch.GetTimestamp();
        Exception? lastError = null;
        bool observedAbsent = false;
        int attempts = 0;
        bool stabilityDelayApplied = false;
        var submenu = menu.OpenMultiplayerSubmenu();
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < RestartSession.AutoJoinTimeoutSeconds)
        {
            if (!ReferenceEquals(_session.Rejoin._clientSession, session))
                return;
            if (session.Outcome != AutoJoinOutcome.Pending)
            {
                FinishAutoJoin(session, started);
                return;
            }

            try
            {
                var hosts = await PlatformUtil.GetFriendsWithOpenLobbies(PlatformUtil.PrimaryPlatform);
                if (!ReferenceEquals(_session.Rejoin._clientSession, session))
                    return;
                bool visible = hosts.Contains(session.HostId);
                if (!visible)
                    observedAbsent = true;
                // Steam exposes friend ids rather than candidate lobby ids. A
                // disappearance is strongest evidence; when polling begins after
                // the replacement is already visible, the confirmed old transport
                // disconnect plus a short grace period is the available fallback.
                bool replacementCandidate = observedAbsent || Stopwatch.GetElapsedTime(started).TotalSeconds >= 0.75;
                if (visible && replacementCandidate)
                {
                    if (!stabilityDelayApplied)
                    {
                        stabilityDelayApplied = true;
                        _session.LogSession(session, "new-lobby-stabilizing", $"oldLobby={session.OldLobbyIdentifier ?? "?"} delayMs=500");
                        await _session.DelayAsync(0.5);
                        if (!ReferenceEquals(_session.Rejoin._clientSession, session))
                            return;
                    }

                    attempts++;
                    _session.LogSession(session, "new-lobby-found", $"oldLobby={session.OldLobbyIdentifier ?? "?"} targetHost={session.HostId} attempt={attempts}");
                    // A failed JoinFlow owns a failed NetClientGameService and
                    // cancellation state. Always ask the native submenu for a
                    // fresh screen/flow rather than reusing that state.
                    var joinScreen = submenu.OnJoinFriendsPressed();
                    await joinScreen.JoinGameAsync(SteamClientConnectionInitializer.FromPlayer(session.HostId));
                    if (!ReferenceEquals(_session.Rejoin._clientSession, session))
                        return;
                    if (session.Outcome != AutoJoinOutcome.Pending)
                    {
                        FinishAutoJoin(session, started);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(_session.Rejoin._clientSession, session))
                    return;
                if (session.Outcome != AutoJoinOutcome.Pending)
                {
                    FinishAutoJoin(session, started);
                    return;
                }

                lastError = ex;
                _session.LogSession(session, "auto-join-attempt-failed", $"attempt={attempts} targetHost={session.HostId} error={ex.GetType().Name}:{ex.Message}");
            }

            await _session.DelayAsync(0.2);
        }

        if (!ReferenceEquals(_session.Rejoin._clientSession, session))
            return;
        if (session.Outcome != AutoJoinOutcome.Pending)
        {
            FinishAutoJoin(session, started);
            return;
        }

        _session.Rejoin._suppressExpectedDisconnect = false;
        _session.LogSession(session, "auto-join-failed", $"attempts={attempts} targetHost={session.HostId} error={lastError?.Message ?? "new lobby not observed"}");
        ClearClientIdentity(session);
        OneButtonNotice.Show("重打", "未能自动加入房主的新大厅，请在联机菜单中手动加入。", "知道了", () =>
        {
        });
    }

    internal bool ConsumeExpectedDisconnectPopup(NetErrorInfo info)
    {
        if (!_session.Rejoin._suppressExpectedDisconnect || _session.Rejoin._clientSession == null)
            return false;
        return info.GetReason()is NetError.Quit or NetError.HostAbandoned;
    }

    internal bool ConsumeExpectedLocalDisconnect(NetErrorInfo info)
    {
        var session = _session.Rejoin._clientSession;
        if (session == null || !session.Committed || !session.ExitStarted)
            return false;
        if (info.GetReason()is not (NetError.Quit or NetError.HostAbandoned))
            return false;
        _session.LogSession(session, "native-disconnect-consumed", $"reason={info.GetReason()}");
        return true;
    }

    internal bool AllowTimeoutOverlayStateChange(NMultiplayerTimeoutOverlay overlay, bool requestedShown)
    {
        var session = _session.Rejoin._clientSession;
        if (!requestedShown || !_session.Rejoin._suppressExpectedDisconnect || session == null)
            return true;
        overlay.Visible = false;
        if (!session.TimeoutOverlaySuppressedLogged)
        {
            session.TimeoutOverlaySuppressedLogged = true;
            _session.LogSession(session, "timeout-overlay-suppressed", "expected-restart-transition");
        }

        return false;
    }

    internal void BeginCommittedClientExit(AutoRejoinSession session)
    {
        if (!ReferenceEquals(_session.Rejoin._clientSession, session) || session.ExitStarted)
            return;
        session.ExitStarted = true;
        _ = ReturnCommittedClientToMainMenuAsync(session);
    }

    internal async Task ReturnCommittedClientToMainMenuAsync(AutoRejoinSession session)
    {
        try
        {
            var game = NGame.Instance;
            if (game == null)
                throw new InvalidOperationException("NGame.Instance is unavailable");
            _session.LogSession(session, "client-exit-start", "native-return-to-main-menu");
            await game.ReturnToMainMenuAfterRun();
            if (ReferenceEquals(_session.Rejoin._clientSession, session))
                _session.LogSession(session, "client-exit-complete", "main-menu-loaded");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_session.Rejoin._clientSession, session))
            {
                session.ExitStarted = false;
                GD.PrintErr($"{RetryMod.LogPrefix}restart committed client exit failed: {ex.Message}");
                _session.LogSession(session, "client-exit-failed", $"error={ex.GetType().Name}:{ex.Message}");
            }
        }
    }

    internal void FinishAutoJoin(AutoRejoinSession session, long searchStarted)
    {
        if (!ReferenceEquals(_session.Rejoin._clientSession, session))
            return;
        switch (session.Outcome)
        {
            case AutoJoinOutcome.Ready:
                _session.LogSession(session, "auto-join-complete", $"searchElapsedMs={_session.ElapsedMs(searchStarted):F0} ready=true");
                ClearClientIdentity(session);
                break;
            case AutoJoinOutcome.JoinedNeedsManualReady:
                _session.LogSession(session, "auto-join-complete", $"searchElapsedMs={_session.ElapsedMs(searchStarted):F0} ready=false");
                string readyError = session.OutcomeDetail ?? "未知错误";
                ClearClientIdentity(session);
                OneButtonNotice.Show("重打", $"已自动加入房主的新大厅，但自动就绪失败：{readyError}\n请手动点击就绪。", "知道了", () =>
                {
                });
                break;
            case AutoJoinOutcome.RejectedNotInSave:
                _session.LogSession(session, "auto-join-rejected", $"searchElapsedMs={_session.ElapsedMs(searchStarted):F0} {session.OutcomeDetail}");
                ClearClientIdentity(session);
                OneButtonNotice.Show("重打", "已找到房主的新大厅，但当前玩家不在续局存档中，无法自动就绪。", "知道了", () =>
                {
                });
                break;
        }
    }

    internal void ClearClientIdentity(AutoRejoinSession? expected = null)
    {
        if (expected != null && !ReferenceEquals(_session.Rejoin._clientSession, expected))
            return;
        _session.Rejoin._clientSession = null;
        _session.Attempt._hostId = 0;
        _session.Attempt._nonceHigh = _session.Attempt._nonceLow = 0;
        _session.Rejoin._oldLobbyIdentifier = null;
        _session.Rejoin._autoRejoinArmed = false;
        _session.Rejoin._suppressExpectedDisconnect = false;
    }
}
