using System.Collections.Generic;

namespace Retry;

internal sealed class SnapshotRunRecord
{
    public long StartTime;
    public string Seed = "";
    public long CapturedAt;
    public List<SnapshotActRecord> Acts { get; } = new();
}
