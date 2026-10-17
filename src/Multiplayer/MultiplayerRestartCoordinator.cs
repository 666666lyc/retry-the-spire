using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;

namespace Retry;

internal static class MultiplayerRestartCoordinator
{
    private static readonly RestartSession Session = new();
    internal enum Phase
    {
        Idle,
        Preparing,
        DirectPreparing,
        AllAcknowledged,
        DirectLoading,
        ReopeningLobby,
        WaitingForPlayers,
        Starting,
    }

    internal enum RestartDecision
    {
        Undecided,
        Direct,
        Lobby,
    }

    internal enum AutoJoinOutcome
    {
        Pending,
        Ready,
        JoinedNeedsManualReady,
        RejectedNotInSave,
    }

    internal static Phase CurrentPhase => Session.Attempt.CurrentPhase;

    internal static void BindToCurrentService() => Session.Capabilities.BindToCurrentService();
    internal static void OnInitialGameInfo(JoinFlow flow, InitialGameInfoMessage message) => Session.Capabilities.OnInitialGameInfo(flow, message);
    internal static void BeginHostPreparation(SerializableRun directSave) => Session.Host.BeginHostPreparation(directSave);
    internal static void OnRawControl(INetGameService net, RestartControlPacket message, ulong senderId) => Session.OnRawControl(net, message, senderId);
    internal static void OnRawDirectPayload(INetGameService net, DirectRestartPayload message, ulong senderId) => Session.OnRawDirectPayload(net, message, senderId);
    internal static void OnClientDisconnected(ulong hostId, NetErrorInfo info) => Session.Client.OnClientDisconnected(hostId, info);
    internal static void OnMainMenuReady(NMainMenu menu) => Session.Client.OnMainMenuReady(menu);
    internal static bool ConsumeExpectedDisconnectPopup(NetErrorInfo info) => Session.Client.ConsumeExpectedDisconnectPopup(info);
    internal static bool ConsumeExpectedLocalDisconnect(NetErrorInfo info) => Session.Client.ConsumeExpectedLocalDisconnect(info);
    internal static bool AllowTimeoutOverlayStateChange(NMultiplayerTimeoutOverlay overlay, bool requestedShown) => Session.Client.AllowTimeoutOverlayStateChange(overlay, requestedShown);
    internal static void AutoReadyClient(LoadRunLobby lobby) => Session.Lobby.AutoReadyClient(lobby);
    internal static void ArmHostAutoStart() => Session.Lobby.ArmHostAutoStart();
    internal static void AbortBeforeDisconnect() => Session.Host.AbortBeforeDisconnect();
    internal static void TryAutoStartHost(LoadRunLobby lobby) => Session.Lobby.TryAutoStartHost(lobby);
    internal static void ResetAfterRunLaunch() => Session.ResetAfterRunLaunch();
    internal static void DirectLoadCompleted() => Session.DirectLoadCompleted();
    internal static void DirectLoadFailed() => Session.DirectLoadFailed();
}
