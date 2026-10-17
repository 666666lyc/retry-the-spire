using System.Collections.Generic;

namespace Retry;

internal sealed class SnapshotActRecord
{
    public int Index;
    public int MapBlobId = -1;
    public List<SnapshotRoomRecord> Rooms { get; } = new();
}
