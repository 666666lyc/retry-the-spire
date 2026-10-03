using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

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

/// <summary>A dedicated reliable message used only between compatible mod peers.</summary>
internal sealed class RestartControlMessage : INetMessage
{
    public const byte CurrentProtocol = 1;

    public byte Protocol = CurrentProtocol;
    public RestartControlKind Kind;
    public uint Generation;
    public ulong NonceHigh;
    public ulong NonceLow;
    public ulong HostId;
    public ulong ActorId;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteByte(Protocol);
        writer.WriteByte((byte)Kind);
        writer.WriteUInt(Generation, 32);
        writer.WriteULong(NonceHigh, 64);
        writer.WriteULong(NonceLow, 64);
        writer.WriteULong(HostId, 64);
        writer.WriteULong(ActorId, 64);
    }

    public void Deserialize(PacketReader reader)
    {
        Protocol = reader.ReadByte();
        Kind = (RestartControlKind)reader.ReadByte();
        Generation = reader.ReadUInt(32);
        NonceHigh = reader.ReadULong(64);
        NonceLow = reader.ReadULong(64);
        HostId = reader.ReadULong(64);
        ActorId = reader.ReadULong(64);
    }
}

/// <summary>
/// Carries the native room-entry save only between v0.4.4+ peers.  Keep this
/// type after <see cref="RestartControlMessage"/> in the message table so the
/// legacy control message retains its v0.4.3 wire id.
/// </summary>
internal sealed class DirectRestartPayloadMessage : INetMessage
{
    public uint Generation;
    public ulong NonceHigh;
    public ulong NonceLow;
    public ulong HostId;
    public ulong ActorId;
    public SerializableRun Run = null!;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(Generation, 32);
        writer.WriteULong(NonceHigh, 64);
        writer.WriteULong(NonceLow, 64);
        writer.WriteULong(HostId, 64);
        writer.WriteULong(ActorId, 64);
        writer.Write(Run);
    }

    public void Deserialize(PacketReader reader)
    {
        Generation = reader.ReadUInt(32);
        NonceHigh = reader.ReadULong(64);
        NonceLow = reader.ReadULong(64);
        HostId = reader.ReadULong(64);
        ActorId = reader.ReadULong(64);
        Run = reader.Read<SerializableRun>();
    }
}

internal static class MultiplayerRestartCoordinator
{
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

    private enum RestartDecision
    {
        Undecided,
        Direct,
        Lobby,
    }

    private enum AutoJoinOutcome
    {
        Pending,
        Ready,
        JoinedNeedsManualReady,
        RejectedNotInSave,
    }

    private sealed class AutoRejoinSession(
        ulong hostId,
        uint generation,
        ulong nonceHigh,
        ulong nonceLow,
        string? oldLobbyIdentifier)
    {
        internal ulong HostId { get; } = hostId;
        internal uint Generation { get; } = generation;
        internal ulong NonceHigh { get; } = nonceHigh;
        internal ulong NonceLow { get; } = nonceLow;
        internal string? OldLobbyIdentifier { get; } = oldLobbyIdentifier;
        internal long Started { get; } = Stopwatch.GetTimestamp();
        internal bool Committed { get; set; }
        internal bool ExitStarted { get; set; }
        internal bool SearchStarted { get; set; }
        internal bool TimeoutOverlaySuppressedLogged { get; set; }
        internal AutoJoinOutcome Outcome { get; set; }
        internal string? OutcomeDetail { get; set; }
        internal RestartDecision Decision { get; set; }
        internal SerializableRun? DirectSave { get; set; }
        internal RunState? DirectState { get; set; }
    }

    private const string CompatibleModPrefix = "RetryTheSpire-";
    private const double PrepareTimeoutSeconds = 3.0;
    private const double ResendSeconds = 0.15;
    private const double DirectResendSeconds = 0.5;
    private const double AutoJoinTimeoutSeconds = 15.0;

    private static readonly HashSet<ulong> CapablePeers = [];
    private static readonly HashSet<ulong> DirectCapablePeers = [];
    private static readonly HashSet<ulong> EverCapablePeers = [];
    private static readonly HashSet<ulong> PendingProbe = [];
    private static readonly HashSet<ulong> PendingPrepare = [];
    private static readonly HashSet<ulong> PreparedPeers = [];
    private static readonly HashSet<ulong> PendingDirectPrepare = [];
    private static readonly Dictionary<ulong, int> SendRounds = [];
    private static readonly object RegistrationLock = new();

    private static bool _messageTypeRegistered;
    private static bool _hostSupportsProtocol;
    private static INetGameService? _registeredService;
    private static uint _generation;
    private static ulong _nonceHigh;
    private static ulong _nonceLow;
    private static ulong _hostId;
    private static long _prepareStarted;
    private static bool _restartRequested;
    private static bool _autoRejoinArmed;
    private static bool _suppressExpectedDisconnect;
    private static string? _oldLobbyIdentifier;
    private static bool _hostAutoStartArmed;
    private static bool _hostReadySent;
    private static bool _directCandidate;
    private static SerializableRun? _directSave;
    private static AutoRejoinSession? _clientSession;

    internal static Phase CurrentPhase { get; private set; } = Phase.Idle;

    internal static void RegisterMessageType()
    {
        lock (RegistrationLock)
        {
            if (_messageTypeRegistered) return;
            try
            {
                var cacheField = typeof(MessageTypes).GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic);
                var cache = cacheField?.GetValue(null);
                if (cache == null) return; // MessageTypes.Initialize postfix will retry.
                var cacheType = cache.GetType();
                var ids = cacheType.GetField("_typeToId", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(cache) as Dictionary<Type, int>;
                var types = cacheType.GetField("_idToType", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(cache) as List<Type>;
                if (ids == null || types == null)
                    throw new InvalidOperationException("native message type cache layout changed");
                // MessageTypes discovers mod INetMessage implementations while
                // initializing and NetTypeCache sorts them by name. That would
                // shift native ids and break an unmodded peer. Rebuild from the
                // game's generated native table, then append mod-only messages.
                var subtypeTable = typeof(MessageTypes).Assembly.GetType(
                    "MegaCrit.Sts2.Core.Multiplayer.Serialization.INetMessageSubtypes");
                var native = subtypeTable?.GetProperty(
                    "All", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(null) as IReadOnlyList<Type>
                    ?? throw new InvalidOperationException("native message subtype table is unavailable");
                var nativeSet = native.ToHashSet();
                var otherModMessages = types
                    .Where(type => !nativeSet.Contains(type)
                        && type != typeof(RestartControlMessage)
                        && type != typeof(DirectRestartPayloadMessage))
                    .OrderBy(type => type.FullName, StringComparer.Ordinal)
                    .ToArray();
                types.Clear();
                types.AddRange(native);
                types.AddRange(otherModMessages);
                // This must remain the first Retry-owned message. v0.4.3 peers
                // already use this exact id for their legacy restart channel.
                types.Add(typeof(RestartControlMessage));
                types.Add(typeof(DirectRestartPayloadMessage));
                ids.Clear();
                for (int i = 0; i < types.Count; i++) ids[types[i]] = i;
                _messageTypeRegistered = true;
                GD.Print(
                    $"{RetryMod.LogPrefix}restart transport registered " +
                    $"controlTypeId={ids[typeof(RestartControlMessage)]} " +
                    $"directTypeId={ids[typeof(DirectRestartPayloadMessage)]} " +
                    $"nativeCount={native.Count} totalCount={types.Count}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}restart transport registration failed: {ex.Message}");
            }
        }
    }

    internal static void BindToCurrentService()
    {
        RegisterMessageType();
        var service = RunManager.Instance?.NetService;
        if (service == null || ReferenceEquals(service, _registeredService)) return;
        if (service.Type == NetGameType.Host)
        {
            CapablePeers.Clear();
            DirectCapablePeers.Clear();
            EverCapablePeers.Clear();
        }
        if (_registeredService != null)
        {
            try { _registeredService.UnregisterMessageHandler<RestartControlMessage>(OnControlMessage); }
            catch { }
            try { _registeredService.UnregisterMessageHandler<DirectRestartPayloadMessage>(OnDirectPayloadMessage); }
            catch { }
        }
        service.RegisterMessageHandler<RestartControlMessage>(OnControlMessage);
        service.RegisterMessageHandler<DirectRestartPayloadMessage>(OnDirectPayloadMessage);
        _registeredService = service;
        GD.Print($"{RetryMod.LogPrefix}restart transport bound local={service.NetId} type={service.Type}");
    }

    internal static void OnInitialGameInfo(JoinFlow flow, InitialGameInfoMessage message)
    {
        // InitialGameInfo arrives while the host is still in its load lobby,
        // before the host has bound our run-time handler. Use it only as a
        // compatibility advertisement; actual negotiation starts after Launch.
        _hostSupportsProtocol = message.otherMods?.Any(IsCompatibleVersion) == true;
        ulong hostId = flow.NetService is NetClientGameService client ? client.HostNetId : 0;
        GD.Print(
            $"{RetryMod.LogPrefix}restart event=host-capability-detected " +
            $"supported={_hostSupportsProtocol} host={hostId} trigger=initial-game-info");
    }

    private static async Task AnnounceCapabilityAsync(INetGameService net, ulong hostId)
    {
        // Both peers bind their handlers from RunManager.Launch. Repeat briefly
        // to tolerate launch-order skew and transient packet loss.
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            if (!net.IsConnected || net.Type != NetGameType.Client) return;
            try
            {
                Send(net, hostId, RestartControlKind.Capability, 0, 0, 0, hostId);
                if (QuickRoomRestart.CanDirectReload)
                    Send(net, hostId, RestartControlKind.DirectCapability, 0, 0, 0, hostId);
                GD.Print(
                    $"{RetryMod.LogPrefix}restart event=capability-send target={hostId} " +
                    $"direct={QuickRoomRestart.CanDirectReload} attempt={attempt}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}restart capability attempt={attempt}: {ex.Message}");
            }
            await DelayAsync(0.35);
        }
    }

    internal static void BeginHostPreparation(SerializableRun directSave)
    {
        if (_restartRequested || CurrentPhase != Phase.Idle) return;
        BindToCurrentService();
        var manager = RunManager.Instance;
        var net = manager?.NetService;
        if (manager?.RunLobby == null || net?.Type != NetGameType.Host)
        {
            QuickRoomRestart.ContinueAfterPreparation();
            return;
        }

        _restartRequested = true;
        CurrentPhase = Phase.Preparing;
        _generation++;
        if (_generation == 0) _generation = 1;
        Span<byte> nonce = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        _nonceHigh = BitConverter.ToUInt64(nonce[..8]);
        _nonceLow = BitConverter.ToUInt64(nonce[8..]);
        _hostId = net.NetId;
        _prepareStarted = Stopwatch.GetTimestamp();
        PendingProbe.Clear();
        PendingPrepare.Clear();
        PreparedPeers.Clear();
        PendingDirectPrepare.Clear();
        SendRounds.Clear();
        _directSave = directSave;

        var connected = manager.RunLobby.ConnectedPlayerIds.Where(id => id != net.NetId).ToHashSet();
        var compatible = CapablePeers.Where(connected.Contains).ToHashSet();
        var undeclared = connected.Where(id => !CapablePeers.Contains(id)).ToHashSet();
        var directCapable = DirectCapablePeers.Where(connected.Contains).ToHashSet();
        _directCandidate = QuickRoomRestart.CanDirectReload && connected.SetEquals(directCapable);
        var lostCapabilities = EverCapablePeers.Where(connected.Contains).Where(id => !CapablePeers.Contains(id)).ToArray();
        Log(
            "prepare-roster",
            $"connected=[{string.Join(',', connected)}] " +
            $"compatible=[{string.Join(',', compatible)}] " +
            $"direct=[{string.Join(',', directCapable)}] " +
            $"directCandidate={_directCandidate} " +
            $"undeclared=[{string.Join(',', undeclared)}]");
        if (lostCapabilities.Length > 0)
        {
            _restartRequested = false;
            CurrentPhase = Phase.Idle;
            string players = string.Join("、", lostCapabilities.Select(PlayerLabel));
            GD.PrintErr(
                $"{RetryMod.LogPrefix}restart capability state lost players={string.Join(',', lostCapabilities)}");
            OneButtonNotice.Show(
                "重打通信异常",
                $"以下成员此前已确认支持自动重连，但当前能力状态丢失：\n{players}\n\n本次重打已取消，请重新尝试。",
                "知道了",
                QuickRoomRestart.CancelPreparation);
            return;
        }
        PendingProbe.UnionWith(compatible);
        PendingPrepare.UnionWith(PendingProbe);
        PreparedPeers.UnionWith(compatible);
        Log(
            "prepare-start",
            $"compatible={PendingPrepare.Count} connected={connected.Count} undeclared={undeclared.Count}");
        _ = PrepareAsync(net);
    }

    private static async Task PrepareAsync(INetGameService net)
    {
        // A separate probe/ACK round is the communication verification gate.
        bool probeOk = await RunAckPhase(net, PendingProbe, RestartControlKind.Probe, RestartControlKind.ProbeAck);
        if (!probeOk)
        {
            if (_directCandidate)
            {
                // Capability packets proved these are new clients, but a
                // missed probe must not prevent responsive peers from arming
                // the existing auto-rejoin fallback.
                PendingPrepare.Clear();
                PendingPrepare.UnionWith(PreparedPeers);
                await RunAckPhase(
                    net, PendingPrepare,
                    RestartControlKind.Prepare, RestartControlKind.PrepareAck);
                await FallBackToLobbyAsync(net, "legacy-probe-timeout");
                return;
            }
            ShowTimeout("通信探测", PendingProbe);
            return;
        }
        bool prepareOk = await RunAckPhase(net, PendingPrepare, RestartControlKind.Prepare, RestartControlKind.PrepareAck);
        if (!prepareOk)
        {
            if (_directCandidate)
            {
                await FallBackToLobbyAsync(net, "legacy-prepare-timeout");
                return;
            }
            ShowTimeout("重打通知", PendingPrepare);
            return;
        }

        if (_directCandidate)
        {
            bool directOk = await PrepareDirectRestartAsync(net);
            if (!directOk)
            {
                await FallBackToLobbyAsync(net, "direct-prepare-timeout");
                return;
            }

            CurrentPhase = Phase.AllAcknowledged;
            Log("direct-all-acknowledged", $"elapsedMs={ElapsedMs(_prepareStarted):F0}");
            await CommitDirectRestartAsync(net);
            return;
        }

        CurrentPhase = Phase.AllAcknowledged;
        Log("all-acknowledged", $"elapsedMs={ElapsedMs(_prepareStarted):F0}");
        await CommitAndContinueAsync(net);
    }

    private static async Task<bool> PrepareDirectRestartAsync(INetGameService net)
    {
        var save = _directSave;
        if (save == null) return false;

        CurrentPhase = Phase.DirectPreparing;
        PendingDirectPrepare.Clear();
        PendingDirectPrepare.UnionWith(DirectCapablePeers.Where(PreparedPeers.Contains));
        long started = Stopwatch.GetTimestamp();
        int round = 0;
        while (PendingDirectPrepare.Count > 0
            && Stopwatch.GetElapsedTime(started).TotalSeconds < PrepareTimeoutSeconds)
        {
            round++;
            foreach (ulong peer in PendingDirectPrepare.ToArray())
            {
                try
                {
                    net.SendMessage(new DirectRestartPayloadMessage
                    {
                        Generation = _generation,
                        NonceHigh = _nonceHigh,
                        NonceLow = _nonceLow,
                        HostId = _hostId,
                        ActorId = net.NetId,
                        Run = save,
                    }, peer);
                    Log("direct-save-send", $"target={peer} round={round}");
                }
                catch (Exception ex)
                {
                    GD.PrintErr(
                        $"{RetryMod.LogPrefix}restart direct save target={peer} " +
                        $"round={round}: {ex.Message}");
                }
            }
            await DelayAsync(DirectResendSeconds);
            if (CurrentPhase != Phase.DirectPreparing) return false;
        }
        Log(
            "direct-prepare-phase",
            $"remaining={string.Join(',', PendingDirectPrepare)} elapsedMs={ElapsedMs(started):F0}");
        return PendingDirectPrepare.Count == 0;
    }

    private static async Task CommitDirectRestartAsync(INetGameService net)
    {
        for (int round = 1; round <= 2; round++)
        {
            foreach (ulong peer in PreparedPeers.Where(DirectCapablePeers.Contains).ToArray())
            {
                try
                {
                    Send(net, peer, RestartControlKind.DirectCommit,
                        _generation, _nonceHigh, _nonceLow, _hostId);
                    Log("direct-commit-send", $"target={peer} round={round}");
                }
                catch (Exception ex)
                {
                    GD.PrintErr(
                        $"{RetryMod.LogPrefix}restart direct commit target={peer} " +
                        $"round={round}: {ex.Message}");
                }
            }
            if (round == 1 && PreparedPeers.Count > 0) await DelayAsync(0.05);
        }

        _restartRequested = false;
        _directSave = null;
        CurrentPhase = Phase.DirectLoading;
        Log("direct-commit-complete", $"targets={PreparedPeers.Count}");
        QuickRoomRestart.ContinueDirectAfterPreparation();
    }

    private static async Task FallBackToLobbyAsync(INetGameService net, string reason)
    {
        Log("direct-fallback", $"reason={reason}");
        PendingProbe.Clear();
        PendingPrepare.Clear();
        PendingDirectPrepare.Clear();
        _directCandidate = false;
        foreach (ulong peer in PreparedPeers.Where(DirectCapablePeers.Contains).ToArray())
        {
            try
            {
                Send(net, peer, RestartControlKind.UseLobbyFallback,
                    _generation, _nonceHigh, _nonceLow, _hostId);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}restart direct fallback target={peer}: {ex.Message}");
            }
        }
        CurrentPhase = Phase.AllAcknowledged;
        await CommitAndContinueAsync(net);
    }

    private static async Task<bool> RunAckPhase(
        INetGameService net, HashSet<ulong> pending,
        RestartControlKind outgoing, RestartControlKind expectedAck)
    {
        if (pending.Count == 0) return true;
        long started = Stopwatch.GetTimestamp();
        while (pending.Count > 0 && Stopwatch.GetElapsedTime(started).TotalSeconds < PrepareTimeoutSeconds)
        {
            foreach (ulong peer in pending.ToArray())
            {
                SendRounds.TryGetValue(peer, out int round);
                SendRounds[peer] = round + 1;
                Send(net, peer, outgoing, _generation, _nonceHigh, _nonceLow, _hostId);
                Log("send", $"kind={outgoing} target={peer} round={round + 1}");
            }
            await DelayAsync(ResendSeconds);
            // ACK handlers remove peers only for the currently active phase/kind.
            if (outgoing == RestartControlKind.Probe && CurrentPhase != Phase.Preparing) return false;
        }
        Log("ack-phase", $"kind={expectedAck} remaining={string.Join(',', pending)} elapsedMs={ElapsedMs(started):F0}");
        return pending.Count == 0;
    }

    private static void ShowTimeout(string stage, HashSet<ulong> pending)
    {
        string ids = pending.Count == 0 ? "未知" : string.Join("、", pending.Select(PlayerLabel));
        Log("timeout", $"stage={stage} pending={string.Join(',', pending)}");
        MultiplayerRestartPrompt.Show(
            $"{stage}未完成，以下成员没有响应：\n{ids}\n\n当前对局仍保持连接。取消后可以再次尝试；只有选择“仍然重开”才会让这些成员改为手动加入。",
            force =>
            {
                PendingProbe.Clear();
                PendingPrepare.Clear();
                _restartRequested = false;
                if (!force)
                {
                    BroadcastCancel();
                    ResetAttempt();
                    QuickRoomRestart.CancelPreparation();
                    return;
                }
                CurrentPhase = Phase.AllAcknowledged;
                var net = RunManager.Instance?.NetService;
                if (net?.Type == NetGameType.Host)
                    _ = CommitAndContinueAsync(net);
                else
                {
                    _restartRequested = false;
                    QuickRoomRestart.ContinueAfterPreparation();
                }
            });
    }

    private static async Task CommitAndContinueAsync(INetGameService net)
    {
        // Commit is an optimization for v0.4.3+ clients: it lets them begin the
        // native main-menu transition before the old Steam connection closes.
        // Older compatible clients ignore this new kind and retain the existing
        // disconnect-driven fallback.
        for (int round = 1; round <= 2; round++)
        {
            foreach (ulong peer in PreparedPeers.ToArray())
            {
                try
                {
                    Send(net, peer, RestartControlKind.CommitRestart,
                        _generation, _nonceHigh, _nonceLow, _hostId);
                    Log("commit-send", $"target={peer} round={round}");
                }
                catch (Exception ex)
                {
                    GD.PrintErr(
                        $"{RetryMod.LogPrefix}restart commit target={peer} round={round}: {ex.Message}");
                }
            }
            if (round == 1 && PreparedPeers.Count > 0) await DelayAsync(0.05);
        }

        _restartRequested = false;
        _directCandidate = false;
        _directSave = null;
        Log("commit-complete", $"targets={PreparedPeers.Count} elapsedMs={ElapsedMs(_prepareStarted):F0}");
        QuickRoomRestart.ContinueAfterPreparation();
    }

    private static void OnControlMessage(RestartControlMessage message, ulong senderId)
    {
        try
        {
            var net = RunManager.Instance?.NetService ?? _registeredService;
            if (net == null || message.Protocol != RestartControlMessage.CurrentProtocol) return;
            if (message.ActorId != senderId || senderId == 0) return;
            Log("receive", $"kind={message.Kind} sender={senderId} actor={message.ActorId} generation={message.Generation}");

            if (message.Kind is RestartControlKind.Capability or RestartControlKind.DirectCapability
                && net.Type == NetGameType.Host)
            {
                if (message.HostId == net.NetId
                    && RunManager.Instance?.RunLobby?.ConnectedPlayerIds.Contains(senderId) == true)
                {
                    bool first = CapablePeers.Add(senderId);
                    EverCapablePeers.Add(senderId);
                    if (message.Kind == RestartControlKind.DirectCapability)
                        DirectCapablePeers.Add(senderId);
                    if (first)
                        Log("capability-received", $"sender={senderId} connected=true");
                    if (message.Kind == RestartControlKind.DirectCapability)
                        Log("direct-capability-received", $"sender={senderId} connected=true");
                }
                return;
            }

            if (net.Type == NetGameType.Host)
            {
                if (message.HostId != net.NetId || message.Generation != _generation
                    || message.NonceHigh != _nonceHigh || message.NonceLow != _nonceLow) return;
                if (message.Kind == RestartControlKind.ProbeAck && PendingProbe.Remove(senderId))
                    Log("ack", $"kind=ProbeAck sender={senderId} remaining={PendingProbe.Count}");
                if (message.Kind == RestartControlKind.PrepareAck && PendingPrepare.Remove(senderId))
                    Log("ack", $"kind=PrepareAck sender={senderId} remaining={PendingPrepare.Count}");
                if (message.Kind == RestartControlKind.DirectPrepareAck
                    && CurrentPhase == Phase.DirectPreparing
                    && PendingDirectPrepare.Remove(senderId))
                    Log("ack", $"kind=DirectPrepareAck sender={senderId} remaining={PendingDirectPrepare.Count}");
                return;
            }

            if (net.Type != NetGameType.Client || net is not NetClientGameService client) return;
            if (senderId != client.HostNetId || message.HostId != senderId) return;
            if (message.Kind == RestartControlKind.Probe)
            {
                Send(net, senderId, RestartControlKind.ProbeAck, message.Generation,
                    message.NonceHigh, message.NonceLow, senderId);
                return;
            }
            if (message.Kind == RestartControlKind.Cancel)
            {
                var session = _clientSession;
                if (session != null
                    && message.Generation == session.Generation
                    && message.NonceHigh == session.NonceHigh
                    && message.NonceLow == session.NonceLow)
                {
                    _suppressExpectedDisconnect = false;
                    LogSession(session, "client-cancelled", "credentials-cleared");
                    ClearClientIdentity(session);
                }
                return;
            }

            if (message.Kind == RestartControlKind.CommitRestart)
            {
                var session = _clientSession;
                if (session == null
                    || session.HostId != senderId
                    || session.Generation != message.Generation
                    || session.NonceHigh != message.NonceHigh
                    || session.NonceLow != message.NonceLow)
                    return;

                if (session.Decision == RestartDecision.Direct) return;

                if (!session.Committed)
                {
                    session.Decision = RestartDecision.Lobby;
                    session.DirectSave = null;
                    session.DirectState = null;
                    session.Committed = true;
                    LogSession(session, "client-commit", "begin-native-main-menu-transition");
                }
                BeginCommittedClientExit(session);
                return;
            }

            if (message.Kind == RestartControlKind.UseLobbyFallback)
            {
                var session = _clientSession;
                if (SessionMatches(session, senderId, message.Generation, message.NonceHigh, message.NonceLow)
                    && !session!.Committed)
                {
                    session.Decision = RestartDecision.Lobby;
                    session.DirectSave = null;
                    session.DirectState = null;
                    LogSession(session, "direct-fallback-received", "awaiting-lobby-commit");
                }
                return;
            }

            if (message.Kind == RestartControlKind.DirectCommit)
            {
                var session = _clientSession;
                if (!SessionMatches(session, senderId, message.Generation, message.NonceHigh, message.NonceLow)
                    || session!.Committed
                    || session.Decision == RestartDecision.Lobby
                    || session.DirectSave == null
                    || session.DirectState == null)
                    return;

                session.Committed = true;
                session.Decision = RestartDecision.Direct;
                _autoRejoinArmed = false;
                _suppressExpectedDisconnect = false;
                LogSession(session, "direct-commit-received", "begin-in-place-load");
                QuickRoomRestart.BeginDirectClientRestart(session.DirectSave, session.DirectState);
                return;
            }

            if (message.Kind != RestartControlKind.Prepare) return;

            // Arm before ACK. Even if the old lobby closes immediately after the
            // ACK reaches the host, the disconnect path can now safely rejoin.
            var existing = _clientSession;
            bool sameSession = existing != null
                && existing.HostId == senderId
                && existing.Generation == message.Generation
                && existing.NonceHigh == message.NonceHigh
                && existing.NonceLow == message.NonceLow;
            var activeSession = sameSession
                ? existing!
                : new AutoRejoinSession(
                    senderId,
                    message.Generation,
                    message.NonceHigh,
                    message.NonceLow,
                    SafeLobbyIdentifier(net));
            _clientSession = activeSession;
            _generation = message.Generation;
            _nonceHigh = message.NonceHigh;
            _nonceLow = message.NonceLow;
            _hostId = senderId;
            _autoRejoinArmed = true;
            _suppressExpectedDisconnect = true;
            _oldLobbyIdentifier = activeSession.OldLobbyIdentifier;
            Send(net, senderId, RestartControlKind.PrepareAck, message.Generation,
                message.NonceHigh, message.NonceLow, senderId);
            LogSession(
                activeSession,
                sameSession ? "client-prepare-duplicate" : "client-armed",
                $"oldLobby={activeSession.OldLobbyIdentifier ?? "?"}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}restart control receive: {ex.Message}");
        }
    }

    private static void OnDirectPayloadMessage(DirectRestartPayloadMessage message, ulong senderId)
    {
        try
        {
            var net = RunManager.Instance?.NetService ?? _registeredService;
            if (net is not NetClientGameService client
                || senderId == 0
                || senderId != client.HostNetId
                || message.ActorId != senderId
                || message.HostId != senderId)
                return;

            var session = _clientSession;
            if (!SessionMatches(
                    session, senderId, message.Generation, message.NonceHigh, message.NonceLow)
                || session!.Committed
                || session.Decision == RestartDecision.Lobby)
                return;

            if (session.DirectSave == null || session.DirectState == null)
            {
                ulong localId = net.NetId;
                if (message.Run.Players.All(p => p.NetId != localId))
                    throw new InvalidOperationException($"local player {localId} is absent from direct restart save");
                session.DirectState = RunState.FromSerializable(message.Run);
                session.DirectSave = message.Run;
                LogSession(session, "direct-save-validated", $"local={localId}");
            }

            Send(net, senderId, RestartControlKind.DirectPrepareAck,
                message.Generation, message.NonceHigh, message.NonceLow, senderId);
        }
        catch (Exception ex)
        {
            // Withhold the ACK. The host will automatically choose the native
            // lobby path after the bounded direct-prepare timeout.
            GD.PrintErr($"{RetryMod.LogPrefix}restart direct save rejected: {ex.Message}");
        }
    }

    private static bool SessionMatches(
        AutoRejoinSession? session,
        ulong hostId,
        uint generation,
        ulong nonceHigh,
        ulong nonceLow) =>
        session != null
        && session.HostId == hostId
        && session.Generation == generation
        && session.NonceHigh == nonceHigh
        && session.NonceLow == nonceLow;

    internal static void OnClientDisconnected(ulong hostId, NetErrorInfo info)
    {
        var session = _clientSession;
        if (session == null || hostId != session.HostId) return;
        if (info.SelfInitiated && !session.Committed) return;
        if (info.GetReason() is not (NetError.Quit or NetError.HostAbandoned)) return;
        LogSession(
            session,
            "old-lobby-disconnected",
            $"reason={info.GetReason()} selfInitiated={info.SelfInitiated}");
    }

    internal static void OnMainMenuReady(NMainMenu menu)
    {
        var session = _clientSession;
        if (session == null || !_autoRejoinArmed || session.HostId == 0 || session.SearchStarted) return;
        session.SearchStarted = true;
        _autoRejoinArmed = false;
        LogSession(session, "main-menu-ready", "auto-search-starting");
        _ = AutoRejoinAsync(menu, session);
    }

    private static async Task AutoRejoinAsync(NMainMenu menu, AutoRejoinSession session)
    {
        long started = Stopwatch.GetTimestamp();
        Exception? lastError = null;
        bool observedAbsent = false;
        int attempts = 0;
        bool stabilityDelayApplied = false;
        var submenu = menu.OpenMultiplayerSubmenu();
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < AutoJoinTimeoutSeconds)
        {
            if (!ReferenceEquals(_clientSession, session)) return;
            if (session.Outcome != AutoJoinOutcome.Pending)
            {
                FinishAutoJoin(session, started);
                return;
            }
            try
            {
                var hosts = await PlatformUtil.GetFriendsWithOpenLobbies(PlatformUtil.PrimaryPlatform);
                if (!ReferenceEquals(_clientSession, session)) return;
                bool visible = hosts.Contains(session.HostId);
                if (!visible) observedAbsent = true;

                // Steam exposes friend ids rather than candidate lobby ids. A
                // disappearance is strongest evidence; when polling begins after
                // the replacement is already visible, the confirmed old transport
                // disconnect plus a short grace period is the available fallback.
                bool replacementCandidate = observedAbsent
                    || Stopwatch.GetElapsedTime(started).TotalSeconds >= 0.75;
                if (visible && replacementCandidate)
                {
                    if (!stabilityDelayApplied)
                    {
                        stabilityDelayApplied = true;
                        LogSession(
                            session,
                            "new-lobby-stabilizing",
                            $"oldLobby={session.OldLobbyIdentifier ?? "?"} delayMs=500");
                        await DelayAsync(0.5);
                        if (!ReferenceEquals(_clientSession, session)) return;
                    }
                    attempts++;
                    LogSession(
                        session,
                        "new-lobby-found",
                        $"oldLobby={session.OldLobbyIdentifier ?? "?"} targetHost={session.HostId} attempt={attempts}");
                    // A failed JoinFlow owns a failed NetClientGameService and
                    // cancellation state. Always ask the native submenu for a
                    // fresh screen/flow rather than reusing that state.
                    var joinScreen = submenu.OnJoinFriendsPressed();
                    await joinScreen.JoinGameAsync(SteamClientConnectionInitializer.FromPlayer(session.HostId));
                    if (!ReferenceEquals(_clientSession, session)) return;
                    if (session.Outcome != AutoJoinOutcome.Pending)
                    {
                        FinishAutoJoin(session, started);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(_clientSession, session)) return;
                if (session.Outcome != AutoJoinOutcome.Pending)
                {
                    FinishAutoJoin(session, started);
                    return;
                }
                lastError = ex;
                LogSession(
                    session,
                    "auto-join-attempt-failed",
                    $"attempt={attempts} targetHost={session.HostId} error={ex.GetType().Name}:{ex.Message}");
            }
            await DelayAsync(0.2);
        }
        if (!ReferenceEquals(_clientSession, session)) return;
        if (session.Outcome != AutoJoinOutcome.Pending)
        {
            FinishAutoJoin(session, started);
            return;
        }
        _suppressExpectedDisconnect = false;
        LogSession(
            session,
            "auto-join-failed",
            $"attempts={attempts} targetHost={session.HostId} error={lastError?.Message ?? "new lobby not observed"}");
        ClearClientIdentity(session);
        OneButtonNotice.Show("重打", "未能自动加入房主的新大厅，请在联机菜单中手动加入。", "知道了", () => { });
    }

    internal static bool ConsumeExpectedDisconnectPopup(NetErrorInfo info)
    {
        if (!_suppressExpectedDisconnect || _clientSession == null) return false;
        return info.GetReason() is NetError.Quit or NetError.HostAbandoned;
    }

    internal static bool ConsumeExpectedLocalDisconnect(NetErrorInfo info)
    {
        var session = _clientSession;
        if (session == null || !session.Committed || !session.ExitStarted) return false;
        if (info.GetReason() is not (NetError.Quit or NetError.HostAbandoned)) return false;
        LogSession(session, "native-disconnect-consumed", $"reason={info.GetReason()}");
        return true;
    }

    internal static bool AllowTimeoutOverlayStateChange(
        NMultiplayerTimeoutOverlay overlay,
        bool requestedShown)
    {
        var session = _clientSession;
        if (!requestedShown || !_suppressExpectedDisconnect || session == null) return true;
        overlay.Visible = false;
        if (!session.TimeoutOverlaySuppressedLogged)
        {
            session.TimeoutOverlaySuppressedLogged = true;
            LogSession(session, "timeout-overlay-suppressed", "expected-restart-transition");
        }
        return false;
    }

    internal static void AutoReadyClient(LoadRunLobby lobby)
    {
        var session = _clientSession;
        if (session == null || lobby.NetService.Type != NetGameType.Client) return;
        if (lobby.NetService is not NetClientGameService client || client.HostNetId != session.HostId) return;
        ulong me = lobby.NetService.NetId;
        LogSession(session, "lobby-entered", $"local={me} lobby={SafeLobbyIdentifier(lobby.NetService) ?? "?"}");
        if (lobby.Run.Players.All(p => p.NetId != me))
        {
            session.Outcome = AutoJoinOutcome.RejectedNotInSave;
            session.OutcomeDetail = $"local={me} absent-from-save";
            _suppressExpectedDisconnect = false;
            LogSession(session, "auto-ready-rejected", session.OutcomeDetail);
            return;
        }
        try
        {
            if (!lobby.IsPlayerReady(me)) lobby.SetReady(true);
            session.Outcome = AutoJoinOutcome.Ready;
            _suppressExpectedDisconnect = false;
            LogSession(session, "auto-ready", $"local={me}");
        }
        catch (Exception ex)
        {
            session.Outcome = AutoJoinOutcome.JoinedNeedsManualReady;
            session.OutcomeDetail = ex.Message;
            _suppressExpectedDisconnect = false;
            GD.PrintErr($"{RetryMod.LogPrefix}restart auto-ready local={me}: {ex.Message}");
            LogSession(session, "auto-ready-failed", $"local={me} error={ex.GetType().Name}:{ex.Message}");
        }
    }

    private static void BeginCommittedClientExit(AutoRejoinSession session)
    {
        if (!ReferenceEquals(_clientSession, session) || session.ExitStarted) return;
        session.ExitStarted = true;
        _ = ReturnCommittedClientToMainMenuAsync(session);
    }

    private static async Task ReturnCommittedClientToMainMenuAsync(AutoRejoinSession session)
    {
        try
        {
            var game = NGame.Instance;
            if (game == null) throw new InvalidOperationException("NGame.Instance is unavailable");
            LogSession(session, "client-exit-start", "native-return-to-main-menu");
            await game.ReturnToMainMenuAfterRun();
            if (ReferenceEquals(_clientSession, session))
                LogSession(session, "client-exit-complete", "main-menu-loaded");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_clientSession, session))
            {
                session.ExitStarted = false;
                GD.PrintErr(
                    $"{RetryMod.LogPrefix}restart committed client exit failed: {ex.Message}");
                LogSession(session, "client-exit-failed", $"error={ex.GetType().Name}:{ex.Message}");
            }
        }
    }

    private static void FinishAutoJoin(AutoRejoinSession session, long searchStarted)
    {
        if (!ReferenceEquals(_clientSession, session)) return;
        switch (session.Outcome)
        {
            case AutoJoinOutcome.Ready:
                LogSession(session, "auto-join-complete", $"searchElapsedMs={ElapsedMs(searchStarted):F0} ready=true");
                ClearClientIdentity(session);
                break;
            case AutoJoinOutcome.JoinedNeedsManualReady:
                LogSession(session, "auto-join-complete", $"searchElapsedMs={ElapsedMs(searchStarted):F0} ready=false");
                string readyError = session.OutcomeDetail ?? "未知错误";
                ClearClientIdentity(session);
                OneButtonNotice.Show(
                    "重打",
                    $"已自动加入房主的新大厅，但自动就绪失败：{readyError}\n请手动点击就绪。",
                    "知道了",
                    () => { });
                break;
            case AutoJoinOutcome.RejectedNotInSave:
                LogSession(session, "auto-join-rejected", $"searchElapsedMs={ElapsedMs(searchStarted):F0} {session.OutcomeDetail}");
                ClearClientIdentity(session);
                OneButtonNotice.Show(
                    "重打",
                    "已找到房主的新大厅，但当前玩家不在续局存档中，无法自动就绪。",
                    "知道了",
                    () => { });
                break;
        }
    }

    internal static void ArmHostAutoStart()
    {
        CurrentPhase = Phase.ReopeningLobby;
        _hostAutoStartArmed = true;
        _hostReadySent = false;
    }

    internal static void AbortBeforeDisconnect()
    {
        BroadcastCancel();
        _restartRequested = false;
        _hostAutoStartArmed = false;
        _hostReadySent = false;
        ResetAttempt();
    }

    private static void BroadcastCancel()
    {
        var manager = RunManager.Instance;
        var net = manager?.NetService;
        if (net?.Type != NetGameType.Host || manager?.RunLobby == null || _hostId == 0) return;
        foreach (ulong peer in CapablePeers.Where(manager.RunLobby.ConnectedPlayerIds.Contains).ToArray())
        {
            try
            {
                Send(net, peer, RestartControlKind.Cancel, _generation, _nonceHigh, _nonceLow, _hostId);
                Log("cancel-send", $"target={peer}");
            }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}restart cancel target={peer}: {ex.Message}"); }
        }
    }

    internal static void TryAutoStartHost(LoadRunLobby lobby)
    {
        if (!_hostAutoStartArmed || _hostReadySent || lobby.NetService.Type != NetGameType.Host) return;
        CurrentPhase = Phase.WaitingForPlayers;
        ulong host = lobby.NetService.NetId;
        ulong[] expected = lobby.Run.Players.Select(p => p.NetId).Distinct().ToArray();
        if (expected.Length == 0 || expected.Any(id => !lobby.ConnectedPlayerIds.Contains(id))) return;
        if (expected.Where(id => id != host).Any(id => !lobby.IsPlayerReady(id))) return;
        _hostReadySent = true;
        _hostAutoStartArmed = false;
        CurrentPhase = Phase.Starting;
        try
        {
            if (!lobby.IsPlayerReady(host)) lobby.SetReady(true);
            Log("host-auto-ready", $"players={expected.Length}");
        }
        catch (Exception ex)
        {
            _hostReadySent = false;
            GD.PrintErr($"{RetryMod.LogPrefix}restart host auto-ready: {ex.Message}");
        }
    }

    internal static void ResetAfterRunLaunch()
    {
        if (CurrentPhase is Phase.Starting or Phase.DirectLoading) CurrentPhase = Phase.Idle;
        BindToCurrentService();
        var net = RunManager.Instance?.NetService;
        if (_hostSupportsProtocol
            && net is NetClientGameService client
            && client.HostNetId != 0)
        {
            GD.Print(
                $"{RetryMod.LogPrefix}restart event=capability-refresh " +
                $"local={net.NetId} host={client.HostNetId} trigger=run-launch");
            _ = AnnounceCapabilityAsync(net, client.HostNetId);
        }
    }

    internal static void DirectLoadCompleted()
    {
        var session = _clientSession;
        if (session?.Decision == RestartDecision.Direct)
            ClearClientIdentity(session);
        ResetAttempt();
        Log("direct-load-complete", "session-cleared");
    }

    internal static void DirectLoadFailed()
    {
        var session = _clientSession;
        if (session?.Decision == RestartDecision.Direct)
            ClearClientIdentity(session);
        ResetAttempt();
    }

    private static void Send(INetGameService net, ulong target, RestartControlKind kind,
        uint generation, ulong nonceHigh, ulong nonceLow, ulong hostId)
    {
        if (target == 0) throw new InvalidOperationException("restart message target is zero");
        net.SendMessage(new RestartControlMessage
        {
            Kind = kind,
            Generation = generation,
            NonceHigh = nonceHigh,
            NonceLow = nonceLow,
            HostId = hostId,
            ActorId = net.NetId,
        }, target);
    }

    private static bool IsCompatibleVersion(string value)
    {
        if (!value.StartsWith(CompatibleModPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return Version.TryParse(value[CompatibleModPrefix.Length..], out var version)
            && version >= new Version(0, 4, 0);
    }

    private static string PlayerLabel(ulong id)
    {
        try { return $"{PlatformUtil.GetPlayerName(PlatformUtil.PrimaryPlatform, id)} ({id})"; }
        catch { return id.ToString(); }
    }

    private static string? SafeLobbyIdentifier(INetGameService net)
    {
        try { return net.GetRawLobbyIdentifier(); }
        catch { return null; }
    }

    private static async Task DelayAsync(double seconds)
    {
        var game = NGame.Instance;
        if (game == null) { await Task.Delay(TimeSpan.FromSeconds(seconds)); return; }
        await game.ToSignal(game.GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    private static void ResetAttempt()
    {
        CurrentPhase = Phase.Idle;
        PendingProbe.Clear();
        PendingPrepare.Clear();
        PendingDirectPrepare.Clear();
        PreparedPeers.Clear();
        SendRounds.Clear();
        _restartRequested = false;
        _directCandidate = false;
        _directSave = null;
        _nonceHigh = _nonceLow = 0;
        _hostId = 0;
        _prepareStarted = 0;
    }

    private static void ClearClientIdentity(AutoRejoinSession? expected = null)
    {
        if (expected != null && !ReferenceEquals(_clientSession, expected)) return;
        _clientSession = null;
        _hostId = 0;
        _nonceHigh = _nonceLow = 0;
        _oldLobbyIdentifier = null;
        _autoRejoinArmed = false;
        _suppressExpectedDisconnect = false;
    }

    private static double ElapsedMs(long started) =>
        started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static void Log(string evt, string detail) => GD.Print(
        $"{RetryMod.LogPrefix}restart event={evt} phase={CurrentPhase} generation={_generation} " +
        $"nonce={_nonceHigh:x16}{_nonceLow:x16} host={_hostId} {detail}");

    private static void LogSession(AutoRejoinSession session, string evt, string detail) => GD.Print(
        $"{RetryMod.LogPrefix}restart event={evt} phase={CurrentPhase} generation={session.Generation} " +
        $"nonce={session.NonceHigh:x16}{session.NonceLow:x16} host={session.HostId} " +
        $"elapsedMs={ElapsedMs(session.Started):F0} {detail}");
}

[HarmonyPatch(typeof(MessageTypes), nameof(MessageTypes.Initialize))]
internal static class RestartProtocol_MessageTypesPatch
{
    static void Postfix() => MultiplayerRestartCoordinator.RegisterMessageType();
}

[HarmonyPatch(typeof(JoinFlow), "HandleInitialGameInfoMessage")]
internal static class RestartProtocol_InitialInfoPatch
{
    static void Postfix(JoinFlow __instance, InitialGameInfoMessage message) =>
        MultiplayerRestartCoordinator.OnInitialGameInfo(__instance, message);
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
    static void Prefix(ulong hostNetId, NetErrorInfo info) =>
        MultiplayerRestartCoordinator.OnClientDisconnected(hostNetId, info);
}

[HarmonyPatch(typeof(RunManager), "LocalPlayerDisconnected")]
internal static class RestartProtocol_LocalDisconnectPatch
{
    static bool Prefix(NetErrorInfo info) =>
        !MultiplayerRestartCoordinator.ConsumeExpectedLocalDisconnect(info);
}

[HarmonyPatch(typeof(NErrorPopup), nameof(NErrorPopup.Create), new[] { typeof(NetErrorInfo) })]
internal static class RestartProtocol_ErrorPopupPatch
{
    static bool Prefix(NetErrorInfo info, ref NErrorPopup __result)
    {
        if (!MultiplayerRestartCoordinator.ConsumeExpectedDisconnectPopup(info)) return true;
        __result = null!;
        return false;
    }
}

[HarmonyPatch(
    typeof(NMultiplayerTimeoutOverlay),
    nameof(NMultiplayerTimeoutOverlay.IsShown),
    MethodType.Setter)]
internal static class RestartProtocol_TimeoutOverlayPatch
{
    static bool Prefix(NMultiplayerTimeoutOverlay __instance, bool value) =>
        MultiplayerRestartCoordinator.AllowTimeoutOverlayStateChange(__instance, value);
}

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), nameof(NMultiplayerLoadGameScreen.InitializeAsClient))]
internal static class RestartProtocol_ClientLobbyPatch
{
    internal static readonly FieldInfo? LobbyField = typeof(NMultiplayerLoadGameScreen).GetField(
        "_runLobby", BindingFlags.Instance | BindingFlags.NonPublic);

    static void Postfix(NMultiplayerLoadGameScreen __instance)
    {
        if (LobbyField?.GetValue(__instance) is LoadRunLobby lobby)
            MultiplayerRestartCoordinator.AutoReadyClient(lobby);
    }
}

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), nameof(NMultiplayerLoadGameScreen.InitializeAsHost))]
internal static class RestartProtocol_HostLobbyPatch
{
    static void Postfix(NMultiplayerLoadGameScreen __instance) => Check(__instance);
    internal static void Check(NMultiplayerLoadGameScreen screen)
    {
        if (RestartProtocol_ClientLobbyPatch.LobbyField?.GetValue(screen) is LoadRunLobby lobby)
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
