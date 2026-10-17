using System.Diagnostics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using static Retry.MultiplayerRestartCoordinator;

namespace Retry;

internal sealed class AutoRejoinSession(ulong hostId, uint generation, ulong nonceHigh, ulong nonceLow, string? oldLobbyIdentifier)
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
