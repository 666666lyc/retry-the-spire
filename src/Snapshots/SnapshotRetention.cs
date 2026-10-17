using System;
using System.Collections.Generic;
using System.Linq;

namespace Retry;

internal static class SnapshotRetention
{
    internal static void Prune(SnapshotData data, int targetBytes, long currentStart, ISet<long> historyStarts, Action<SnapshotRunRecord> removed)
    {
        var candidates = data.Runs.Where(r => r.StartTime != currentStart).OrderBy(r => historyStarts.Contains(r.StartTime)).ThenBy(r => r.CapturedAt).ToList();
        foreach (var run in candidates)
        {
            if (SnapshotCodec.EncodedSize(data) <= targetBytes)
                break;
            data.Runs.Remove(run);
            removed(run);
        }
    }
}
