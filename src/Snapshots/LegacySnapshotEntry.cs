using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Rngs;

namespace Retry;

internal sealed class LegacySnapshotEntry
{
    public int Row { get; set; }
    public int Col { get; set; }
    public Dictionary<RunRngType, int> Counters { get; set; } = new();
    public string? PlayersJson { get; set; }
    public long CapturedAt { get; set; }
}
