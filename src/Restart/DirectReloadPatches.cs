using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace Retry;

[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.Disconnect), new[] { typeof(NetError), typeof(bool) })]
internal static class DirectRestartClientDisconnectPatch
{
    private static bool Prefix(NetError reason, bool now) => AllowOrSuppressDisconnect("client", reason, now);
    private static bool AllowOrSuppressDisconnect(string side, NetError reason, bool now)
    {
        if (!QuickRoomRestart.IsNetworkPreservingCleanup)
            return true;
        GD.Print($"{RetryMod.LogPrefix}quick restart: suppressed {side} disconnect " + $"during direct cleanup reason={reason} now={now}");
        return false;
    }
}

[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.Disconnect), new[] { typeof(NetError), typeof(bool) })]
internal static class DirectRestartHostDisconnectPatch
{
    private static bool Prefix(NetError reason, bool now)
    {
        if (!QuickRoomRestart.IsNetworkPreservingCleanup)
            return true;
        GD.Print($"{RetryMod.LogPrefix}quick restart: suppressed host disconnect " + $"during direct cleanup reason={reason} now={now}");
        return false;
    }
}

internal sealed class DirectLoadRunLobbyListener : ILoadRunLobbyListener
{
    internal static DirectLoadRunLobbyListener Instance { get; } = new();

    public void BeginRun()
    {
    }

    public void LocalPlayerDisconnected(NetErrorInfo info)
    {
    }

    public void PlayerConnected(ulong playerId)
    {
    }

    public void PlayerReadyChanged(ulong playerId)
    {
    }

    public void RemotePlayerDisconnected(ulong playerId)
    {
    }

    public Task<bool> ShouldAllowRunToBegin() => Task.FromResult(true);
}
