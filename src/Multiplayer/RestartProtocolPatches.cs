using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

internal enum RestartControlKind : byte
{
    Capability = 1,
    Probe = 2,
    ProbeAck = 3,
    Prepare = 4,
    PrepareAck = 5,
    Cancel = 6,
    CommitRestart = 7,
    DirectCapability = 8,
    DirectPrepareAck = 9,
    DirectCommit = 10,
    UseLobbyFallback = 11,
}

[HarmonyPatch(typeof(JoinFlow), "HandleInitialGameInfoMessage")]
internal static class RestartProtocol_InitialInfoPatch
{
    static void Postfix(JoinFlow __instance, InitialGameInfoMessage message) => MultiplayerRestartCoordinator.OnInitialGameInfo(__instance, message);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
internal static class RestartProtocol_RunLaunchPatch
{
    static void Postfix() => MultiplayerRestartCoordinator.ResetAfterRunLaunch();
}

[HarmonyPatch(typeof(NMainMenu), "_Ready")]
internal static class RestartProtocol_MainMenuPatch
{
    static void Postfix(NMainMenu __instance) => MultiplayerRestartCoordinator.OnMainMenuReady(__instance);
}

[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.OnDisconnectedFromHost))]
internal static class RestartProtocol_DisconnectPatch
{
    static void Prefix(ulong hostNetId, NetErrorInfo info) => MultiplayerRestartCoordinator.OnClientDisconnected(hostNetId, info);
}

[HarmonyPatch(typeof(RunManager), "LocalPlayerDisconnected")]
internal static class RestartProtocol_LocalDisconnectPatch
{
    static bool Prefix(NetErrorInfo info) => !MultiplayerRestartCoordinator.ConsumeExpectedLocalDisconnect(info);
}

[HarmonyPatch(typeof(NErrorPopup), nameof(NErrorPopup.Create), new[] { typeof(NetErrorInfo) })]
internal static class RestartProtocol_ErrorPopupPatch
{
    static bool Prefix(NetErrorInfo info, ref NErrorPopup __result)
    {
        if (!MultiplayerRestartCoordinator.ConsumeExpectedDisconnectPopup(info))
            return true;
        __result = null !;
        return false;
    }
}

[HarmonyPatch(typeof(NMultiplayerTimeoutOverlay), nameof(NMultiplayerTimeoutOverlay.IsShown), MethodType.Setter)]
internal static class RestartProtocol_TimeoutOverlayPatch
{
    static bool Prefix(NMultiplayerTimeoutOverlay __instance, bool value) => MultiplayerRestartCoordinator.AllowTimeoutOverlayStateChange(__instance, value);
}

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), nameof(NMultiplayerLoadGameScreen.InitializeAsClient))]
internal static class RestartProtocol_ClientLobbyPatch
{
    internal static readonly FieldInfo? LobbyField = typeof(NMultiplayerLoadGameScreen).GetField("_runLobby", BindingFlags.Instance | BindingFlags.NonPublic);
    static void Postfix(NMultiplayerLoadGameScreen __instance)
    {
        if (LobbyField?.GetValue(__instance)is LoadRunLobby lobby)
            MultiplayerRestartCoordinator.AutoReadyClient(lobby);
    }
}

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), nameof(NMultiplayerLoadGameScreen.InitializeAsHost))]
internal static class RestartProtocol_HostLobbyPatch
{
    static void Postfix(NMultiplayerLoadGameScreen __instance) => Check(__instance);
    internal static void Check(NMultiplayerLoadGameScreen screen)
    {
        if (RestartProtocol_ClientLobbyPatch.LobbyField?.GetValue(screen)is LoadRunLobby lobby)
            MultiplayerRestartCoordinator.TryAutoStartHost(lobby);
    }
}

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), nameof(NMultiplayerLoadGameScreen.PlayerConnected))]
internal static class RestartProtocol_PlayerConnectedPatch
{
    static void Postfix(NMultiplayerLoadGameScreen __instance) => RestartProtocol_HostLobbyPatch.Check(__instance);
}

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), nameof(NMultiplayerLoadGameScreen.PlayerReadyChanged))]
internal static class RestartProtocol_PlayerReadyPatch
{
    static void Postfix(NMultiplayerLoadGameScreen __instance) => RestartProtocol_HostLobbyPatch.Check(__instance);
}
