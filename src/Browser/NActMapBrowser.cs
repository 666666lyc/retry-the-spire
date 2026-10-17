using System;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

public static class NActMapBrowser
{
    private static readonly BrowserSession Session = new();
    public static event Action? ReadyForAutomation { add => Session.ReadyForAutomation += value; remove => Session.ReadyForAutomation -= value; }

    public static bool Active => Session.Active;
    public static NRun? SpawnedNRun => Session.SpawnedNRun;
    public static MapCoord? SelectedCoord => Session.SelectedCoord;

    // Kept as a field for compatibility with existing consumers and patches.
    public static ulong? NetServiceNetIdOverride;
    public static void Open(RunHistory history, RunHistoryPlayer player) => Session.Open(history, player);
    public static void Close() => Session.Close();
    public static void HandleMapPointClick(MapCoord coord) => Session.View.HandleMapPointClick(coord);
    internal static void NotifyMultiplayerLaunchFailed() => Session.NotifyMultiplayerLaunchFailed();
}
