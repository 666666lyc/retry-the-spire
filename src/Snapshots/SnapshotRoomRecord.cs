using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Rngs;

namespace Retry;

internal sealed class SnapshotRoomRecord
{
    public int Floor;
    public int Row;
    public int Col;
    public Dictionary<RunRngType, int> Counters { get; set; } = new();
    public List<int> PlayerBlobIds { get; set; } = new();
}
