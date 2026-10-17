using System.Collections.Generic;
using MegaCrit.Sts2.Core.Saves;
using static Retry.MultiplayerRestartCoordinator;

namespace Retry;

internal sealed class RestartAttemptState
{
    internal readonly HashSet<ulong> PendingProbe = [];
    internal readonly HashSet<ulong> PendingPrepare = [];
    internal readonly HashSet<ulong> PreparedPeers = [];
    internal readonly HashSet<ulong> PendingDirectPrepare = [];
    internal readonly Dictionary<ulong, int> SendRounds = [];
    internal uint _generation;
    internal ulong _nonceHigh;
    internal ulong _nonceLow;
    internal ulong _hostId;
    internal long _prepareStarted;
    internal bool _restartRequested;
    internal bool _hostAutoStartArmed;
    internal bool _hostReadySent;
    internal bool _directCandidate;
    internal SerializableRun? _directSave;
    internal Phase CurrentPhase { get; set; } = Phase.Idle;
}
