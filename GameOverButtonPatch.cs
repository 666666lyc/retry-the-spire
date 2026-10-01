// Drop a "View Acts" banner on the death/win screen so the player
// can jump straight into the act-map browser for the run that just
// ended. Mirrors the banner on NRunHistory but tinted gold and
// pinned to the left edge so it doesn't fight the game's existing
// Continue / View Run / Main Menu button column on the right.
//
// Banner click → grab the screen's _history private field (set by
// NGameOverScreen._Ready from RunManager.Instance.History) → open
// NActMapBrowser with it.
using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace Retry;

[HarmonyPatch(typeof(NGameOverScreen), "_Ready")]
public static class NGameOverScreen_Ready_Patch
{
    private const string ButtonName = "RetryTheSpireViewActMapsButton";

    private static readonly FieldInfo? HistoryField =
        typeof(NGameOverScreen).GetField("_history",
            BindingFlags.Instance | BindingFlags.NonPublic);

    static void Postfix(NGameOverScreen __instance)
    {
        if (!RetryMod.Enabled) return;
        try
        {
            if (__instance.HasMeta("retry_the_spire_gameover_button_wired")) return;
            __instance.SetMeta("retry_the_spire_gameover_button_wired", true);
            AddBanner(__instance);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}game over banner: {ex.Message}");
        }
    }

    private static void AddBanner(NGameOverScreen screen)
    {
        var captured = screen;
        var root = BannerButton.Create(new BannerButtonOptions
        {
            Name = ButtonName,
            Text = "View Acts",
            // Left side so it doesn't fight the right-anchored button
            // stack (Continue / View Run / Main Menu / Leaderboard).
            Side = BannerSide.Left,
            // Gold-ish — matches the "achievement" tone of a finished
            // run rather than the cool blue of the in-menu browser.
            Tint = new Color(0.88f, 0.66f, 0.18f, 1f),
            Brightness = 2.1f,
            // Pinned above mid-screen — far enough from the top banner
            // ("YOU DIED" / victory text) and the side button column.
            OffsetTop = 360,
            OnClick = () => OpenBrowser(captured),
        });
        screen.AddChild(root);
    }

    private static void OpenBrowser(NGameOverScreen screen)
    {
        try
        {
            // Prefer the run's archived history (written when the run
            // ended). Falls back to the live RunManager.History which
            // NGameOverScreen._Ready also reads.
            var history = HistoryField?.GetValue(screen) as RunHistory
                          ?? RunManager.Instance?.History;
            if (history == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}game over banner: no history available");
                return;
            }
            if (history.Players.Count == 0)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}game over banner: history has no players");
                return;
            }
            var player = history.Players[0];
            _ = OpenViaMainMenuAsync(history, player);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}game over banner OpenBrowser: {ex.Message}");
        }
    }

    // Game-over case: the run has ended but the state isn't torn
    // down yet (game-over screen renders on top of the live NRun).
    // Clean up first, then run through the shared transition that
    // ends with the browser open over a fresh NMainMenu + NRunHistory.
    private static async System.Threading.Tasks.Task OpenViaMainMenuAsync(
        RunHistory history,
        RunHistoryPlayer player)
    {
        try
        {
            // Drop the in-progress run state and kill any disk save so
            // RetryRunner's hasSave guard doesn't pop a second modal
            // when the user confirms a retry from the browser.
            try { RunManager.Instance?.CleanUp(graceful: false); }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}game over banner CleanUp: {ex.Message}"); }
            try { MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.DeleteCurrentRun(); }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}game over banner DeleteCurrentRun: {ex.Message}"); }

            await BrowserViaMainMenu.OpenAsync(history, player);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}game over banner OpenViaMainMenuAsync: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
