using System.Collections.Generic;

namespace Retry;

internal sealed class LegacySnapshotData
{
    public int Version { get; set; }
    public Dictionary<string, LegacySnapshotEntry> Entries { get; set; } = new();
}
