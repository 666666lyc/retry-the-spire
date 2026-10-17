using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

internal sealed class RestartCapabilities
{
    private readonly RestartSession _session;
    internal RestartCapabilities(RestartSession session) => _session = session;
    internal void BindToCurrentService()
    {
        var service = RunManager.Instance?.NetService;
        if (service == null || ReferenceEquals(service, _session.Connection._boundService))
            return;
        if (_session.Connection._boundService != null)
        {
            ClearPeerCapabilities();
            _session.ResetAttempt();
        }

        if (!ReferenceEquals(service, _session.Connection._advertisedService))
            ClearHostAdvertisement();
        _session.Connection._boundService = service;
        GD.Print($"{RetryMod.LogPrefix}restart raw transport bound local={service.NetId} type={service.Type}");
    }

    internal void OnInitialGameInfo(JoinFlow flow, InitialGameInfoMessage message)
    {
        // InitialGameInfo arrives while the host is still in its load lobby,
        // before the host has bound our run-time handler. Use it only as a
        // compatibility advertisement; actual negotiation starts after Launch.
        _session.Connection._hostSupportsProtocol = message.otherMods?.Any(IsCompatibleVersion) == true;
        ulong hostId = flow.NetService is NetClientGameService client ? client.HostNetId : 0;
        _session.Connection._advertisedHostId = hostId;
        _session.Connection._advertisedService = flow.NetService;
        GD.Print($"{RetryMod.LogPrefix}restart event=host-capability-detected " + $"supported={_session.Connection._hostSupportsProtocol} host={hostId} trigger=initial-game-info");
    }

    internal async Task AnnounceCapabilityAsync(INetGameService net, ulong hostId)
    {
        // Both peers bind their handlers from RunManager.Launch. Repeat briefly
        // to tolerate launch-order skew and transient packet loss.
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            if (!net.IsConnected || net.Type != NetGameType.Client)
                return;
            try
            {
                _session.Send(net, hostId, RestartControlKind.Capability, 0, 0, 0, hostId);
                if (QuickRoomRestart.CanDirectReload)
                    _session.Send(net, hostId, RestartControlKind.DirectCapability, 0, 0, 0, hostId);
                GD.Print($"{RetryMod.LogPrefix}restart event=capability-send target={hostId} " + $"direct={QuickRoomRestart.CanDirectReload} attempt={attempt}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}restart capability attempt={attempt}: {ex.Message}");
            }

            await _session.DelayAsync(0.35);
        }
    }

    internal bool IsCompatibleVersion(string value)
    {
        if (!value.StartsWith(RestartSession.CompatibleModPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return Version.TryParse(value[RestartSession.CompatibleModPrefix.Length..], out var version) && version >= new Version(0, 5, 0);
    }

    internal void ClearPeerCapabilities()
    {
        _session.Connection.CapablePeers.Clear();
        _session.Connection.DirectCapablePeers.Clear();
        _session.Connection.EverCapablePeers.Clear();
    }

    internal void ClearHostAdvertisement()
    {
        _session.Connection._hostSupportsProtocol = false;
        _session.Connection._advertisedHostId = 0;
        _session.Connection._advertisedService = null;
    }

    internal void ClearNetworkBinding()
    {
        ClearPeerCapabilities();
        _session.ResetAttempt();
        ClearHostAdvertisement();
        _session.Connection._boundService = null;
    }
}
