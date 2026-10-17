using System.Collections.Generic;

namespace Retry;

internal sealed class SnapshotData
{
    public List<byte[]> Maps { get; } = new();
    public List<byte[]> Players { get; } = new();
    public List<SnapshotRunRecord> Runs { get; } = new();
}
