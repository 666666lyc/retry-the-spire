using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

public static class HudResync
{
    private static readonly FieldInfo? RelicNodesField = typeof(NRelicInventory).GetField("_relicNodes", BindingFlags.NonPublic | BindingFlags.Instance);
    public static void Apply(RunState runState)
    {
        try
        {
            var nrun = NRun.Instance;
            if (nrun == null)
            {
                GD.Print($"{RetryMod.LogPrefix}hud-resync: NRun.Instance null — skipping");
                return;
            }

            var globalUi = nrun.GlobalUi;
            if (globalUi == null)
                return;
            // Clear + rebuild the relic inventory.
            var inv = globalUi.RelicInventory;
            if (inv != null && RelicNodesField?.GetValue(inv)is System.Collections.IList nodes)
            {
                var snapshot = new System.Collections.Generic.List<object>();
                foreach (var n in nodes)
                    snapshot.Add(n);
                foreach (var n in snapshot)
                {
                    if (n is Node node)
                    {
                        try
                        {
                            inv.RemoveChild(node);
                            node.QueueFree();
                        }
                        catch
                        {
                        }
                    }
                }

                nodes.Clear();
                // Re-Initialize repopulates from player.Relics. Safe
                // to call because we just emptied the visual list.
                try
                {
                    inv.Initialize(runState);
                }
                catch (System.Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}hud-resync inv init: {ex.Message}");
                }
            }

            // Re-init JUST the deck-count badge — full
            // TopBar.Initialize would also re-add potions which
            // throws ("Slot already contains a potion") because
            // they're already in the player's potion slots after
            // our injection.
            try
            {
                var deckBtn = globalUi.TopBar?.Deck;
                var player = runState.Players.Count > 0 ? runState.Players[0] : null;
                if (deckBtn != null && player != null)
                    deckBtn.Initialize(player);
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}hud-resync deck btn: {ex.Message}");
            }

            // Map screen: SetMap was first called during EnterAct
            // BEFORE we marked any intermediate coords as visited,
            // so the visited-path darkening loop inside SetMap ran
            // over an empty list. Re-run SetMap to redraw the map
            // with the now-correct VisitedMapCoords (this also
            // resets the position marker, which we re-init below).
            try
            {
                var map = runState.Map;
                var mapScreen = globalUi.MapScreen;
                if (map != null && mapScreen != null)
                {
                    mapScreen.SetMap(map, runState.Rng.Seed, clearDrawings: false);
                    mapScreen.RefreshAllMapPointVotes();
                    // Marker: EnterAct(0) pointed it at the
                    // StartingMapPoint (Ancient) BEFORE our retry
                    // suppression. Move it to the last coord the
                    // player actually visited — i.e. the target
                    // we just entered.
                    var visited = runState.VisitedMapCoords;
                    if (visited.Count > 0)
                    {
                        mapScreen.InitMarker(visited[visited.Count - 1]);
                    }
                }
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}hud-resync map: {ex.Message}");
            }

            GD.Print($"{RetryMod.LogPrefix}hud-resync: rebuilt");
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}hud-resync: {ex.Message}");
        }
    }
}
