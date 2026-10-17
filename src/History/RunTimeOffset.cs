// Offset RunManager's in-run timer so a retry doesn't display as
// 0:00. The original run records only a total RunTime (seconds) and
// a start timestamp — no per-floor breakdown — so we estimate the
// elapsed time at the target node by pro-rating total time by floor
// position. The historical death node (last visited entry in the
// last act) gets the full original RunTime, matching the user's
// "if you pick the node you died on, start with the full timer"
// preference.
//
// Implementation: RunManager.RunTime is computed as
//   now - _sessionStartTime + _prevRunTime
// _sessionStartTime is set to "now" during InitializeShared, and
// _prevRunTime is loaded from the SerializableRun (0 for a fresh
// run). Both are private — we set _prevRunTime via reflection.
using System;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

public static class RunTimeOffset
{
    private static readonly FieldInfo? PrevRunTimeField = typeof(RunManager).GetField("_prevRunTime", BindingFlags.NonPublic | BindingFlags.Instance);
    public static void Apply(RetryTarget target)
    {
        if (PrevRunTimeField == null)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}timer offset: _prevRunTime field missing");
            return;
        }

        var rm = RunManager.Instance;
        if (rm == null)
            return;
        long offsetSeconds = ComputeOffsetSeconds(target);
        PrevRunTimeField.SetValue(rm, offsetSeconds);
        GD.Print($"{RetryMod.LogPrefix}timer offset: {offsetSeconds}s ({FormatHMS(offsetSeconds)})");
    }

    private static long ComputeOffsetSeconds(RetryTarget target)
    {
        float totalSeconds = Math.Max(0f, target.OriginalRunTime);
        int totalFloors = target.OriginalTotalFloors;
        if (totalFloors <= 0 || totalSeconds <= 0f)
            return 0;
        // The truncated history-so-far on target ends at (and includes)
        // the target floor, so summing it across acts gives the
        // 1-indexed global floor number of the target. Subtract one
        // for the 0-indexed global floor.
        int globalFloor = 0;
        foreach (var act in target.MapPointHistorySoFar)
            globalFloor += act.Count;
        globalFloor = Math.Max(0, globalFloor - 1);
        // Death node = last historical floor visited. Use the full
        // original RunTime for it; otherwise pro-rate.
        if (globalFloor >= totalFloors - 1)
            return (long)Math.Floor(totalSeconds);
        double fraction = (double)globalFloor / totalFloors;
        return (long)Math.Floor(totalSeconds * fraction);
    }

    private static string FormatHMS(long secs)
    {
        if (secs < 0)
            secs = 0;
        long h = secs / 3600;
        long m = (secs % 3600) / 60;
        long s = secs % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }
}
