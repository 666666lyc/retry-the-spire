// Shared "transition to main-menu state, push NRunHistory submenu,
// open the act-map browser on top" flow. Used by both the game-over
// banner (where the run already ended) and the mid-run View Acts
// banner (after the abandon modal completes), so both paths land
// the user in the exact same submenu state as if they'd navigated
// by hand through Compendium → Run History → click that run →
// click View Acts.
//
// Caller is responsible for tearing down the live run state BEFORE
// invoking this (CleanUp + DeleteCurrentRun, or PerformAbandon).
// We assume RunManager.State is already null when this runs.
//
// Visible timing: game-screen → quick black fade → browser. The
// scene swap + submenu push happen behind the still-opaque
// NTransition so there's no main-menu flash. FadeIn is kicked off
// after the browser is up (fire-and-forget) so when the user backs
// out, the menu underneath is already clear.
using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

public static class BrowserViaMainMenu
{
    public static async Task OpenAsync(RunHistory history, RunHistoryPlayer player)
    {
        try
        {
            var ng = NGame.Instance;
            if (ng == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}BrowserViaMainMenu: NGame.Instance null");
                return;
            }

            // 1) Quick fade-out covers the upcoming scene swap.
            try
            {
                await ng.Transition.FadeOut(0.2f);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}BrowserViaMainMenu FadeOut: {ex.Message}");
            }

            // 2) Switch to NMainMenu scene.
            try
            {
                var menuScene = MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu.Create(openTimeline: false);
                ng.RootSceneContainer?.SetCurrentScene(menuScene);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}BrowserViaMainMenu SetCurrentScene: {ex.Message}");
            }

            // Wait one frame for NMainMenu's _Ready to wire up.
            await ng.ToSignal(ng.GetTree(), Godot.SceneTree.SignalName.ProcessFrame);
            // 3) Push NRunHistory submenu; its OnSubmenuOpened auto-
            //    selects index 0 (most recent run). For the game-over
            //    flow that's the run we just played. For the mid-run
            //    flow this also defaults to the most recent run, but
            //    the back button from the browser will land here and
            //    the user can navigate to whichever run they wanted.
            if (ng.RootSceneContainer?.CurrentScene is MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu menu && menu.SubmenuStack != null)
            {
                try
                {
                    menu.SubmenuStack.PushSubmenuType<MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen.NRunHistory>();
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}BrowserViaMainMenu submenu push: {ex.Message}");
                }
            }

            await ng.ToSignal(ng.GetTree(), Godot.SceneTree.SignalName.ProcessFrame);
            // 4) Open browser on top of the still-faded screen.
            GD.Print($"{RetryMod.LogPrefix}BrowserViaMainMenu: opening browser for seed={history.Seed}");
            NActMapBrowser.Open(history, player);
            // 5) Clear the fade behind the browser (fire-and-forget).
            _ = ng.Transition.FadeIn(0.3f);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}BrowserViaMainMenu OpenAsync: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
