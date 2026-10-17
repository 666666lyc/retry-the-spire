// Shared reflection helper for reaching RunManager's private State
// property from inside Harmony patches. Caching the PropertyInfo
// keeps the hot path (every retry-launched run) off the reflection
// machinery.
using System.Reflection;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

internal static class StateAccessor
{
    private static readonly PropertyInfo? StateProperty = typeof(RunManager).GetProperty("State", BindingFlags.NonPublic | BindingFlags.Instance);
    public static RunState? GetState(RunManager runManager)
    {
        return StateProperty?.GetValue(runManager) as RunState;
    }
}
