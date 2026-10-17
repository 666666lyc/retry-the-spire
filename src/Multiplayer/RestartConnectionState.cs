using System.Collections.Generic;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace Retry;

internal sealed class RestartConnectionState
{
    internal readonly HashSet<ulong> CapablePeers = [];
    internal readonly HashSet<ulong> DirectCapablePeers = [];
    internal readonly HashSet<ulong> EverCapablePeers = [];
    internal bool _hostSupportsProtocol;
    internal ulong _advertisedHostId;
    internal INetGameService? _advertisedService;
    internal INetGameService? _boundService;
}
