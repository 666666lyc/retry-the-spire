// Add a banner-style "View Act Maps" button to the run-history
// screen. Mirrors the Run Table mod's "Browse Runs" banner but
// lives on the LEFT side (notches pointing into the screen) and is
// tinted blue instead of green. Click → spawn NActMapBrowser as an
// overlay over the current screen; the browser owns its own close
// button so we land back here when the user dismisses it.
//
// Design notes (matches Run Table's Browse Runs button):
//   • Banner texture is the same back_button.tres atlas the game's
//     own NBackButton uses, plus the same outline texture for the
//     hover halo.
//   • A recolor shader (luminance × tint) lets us pick any hue while
//     keeping the texture's grain — straight Modulate × blue would
//     muddy the warm tones baked into the source art.
//   • The whole banner is shifted off the LEFT edge so the pennant
//     tail hangs off-screen.
//   • Vertical position is re-derived from the icon-row geometry at
//     runtime, retried across frames until the layout settles.
using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace Retry;

[HarmonyPatch(typeof(NRunHistory), "_Ready")]
public static class NRunHistory_Ready_Patch
{
    private const string ButtonName = "RetryTheSpireViewActMapsButton";
    private const string BannerTexPath  = "res://images/atlases/ui_atlas.sprites/back_button.tres";
    private const string OutlineTexPath = "res://images/atlases/compressed.sprites/back_button_outline.tres";
    private const string KreonBoldTooltipPath = "res://themes/kreon_bold_glyph_space_one.tres";

    private static readonly FieldInfo? HistoryField =
        typeof(NRunHistory).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic);

    static void Postfix(NRunHistory __instance)
    {
        if (!RetryMod.Enabled) return;
        try
        {
            if (__instance.HasMeta("retry_the_spire_maps_button_wired")) return;
            __instance.SetMeta("retry_the_spire_maps_button_wired", true);
            AddBannerButton(__instance);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}history maps button: {ex.Message}");
        }
    }

    private static void AddBannerButton(NRunHistory rh)
    {
        var captured = rh;
        var root = BannerButton.Create(new BannerButtonOptions
        {
            Name = ButtonName,
            Text = "View Acts",
            Side = BannerSide.Right,
            Tint = new Color(0.20f, 0.40f, 0.78f, 1f),
            Brightness = 2.1f,
            OffsetTop = 280,
            OnClick = () => OpenBrowser(captured),
        });
        rh.AddChild(root);
        ScheduleAlign(root, rh, 0);
    }

    private static void ScheduleAlign(Control btn, NRunHistory rh, int attempt)
    {
        if (attempt > 30) return;
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(btn) || !GodotObject.IsInstanceValid(rh)) return;
            bool aligned = AlignToIconRow(btn, rh);
            if (!aligned) ScheduleAlign(btn, rh, attempt + 1);
        }).CallDeferred();
    }

    private static bool AlignToIconRow(Control btn, NRunHistory rh)
    {
        try
        {
            var mph = rh.GetNodeOrNull<NMapPointHistory>("%MapPointHistory");
            if (mph == null) return false;
            var firstEntry = FindFirstEntry(mph);
            if (firstEntry == null) return false;
            var rect = firstEntry.GetGlobalRect();
            if (rect.Size.Y <= 1f) return false;
            float centerYGlobal = rect.Position.Y + rect.Size.Y * 0.5f;
            float centerYLocal = centerYGlobal - rh.GlobalPosition.Y;
            if (centerYLocal < 0 || centerYLocal > 4000) return false;
            float h = btn.OffsetBottom - btn.OffsetTop;
            if (h <= 0) h = 100;
            btn.OffsetTop = centerYLocal - h * 0.5f;
            btn.OffsetBottom = centerYLocal + h * 0.5f;
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}AlignToIconRow: {ex.Message}");
            return false;
        }
    }

    private static NMapPointHistoryEntry? FindFirstEntry(Godot.Node root)
    {
        foreach (var ch in root.GetChildren())
        {
            if (ch is NMapPointHistoryEntry e && GodotObject.IsInstanceValid(e)) return e;
            var deep = FindFirstEntry(ch);
            if (deep != null) return deep;
        }
        return null;
    }

    private static void OpenBrowser(NRunHistory screen)
    {
        try
        {
            if (HistoryField?.GetValue(screen) is not RunHistory history)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}history maps: no _history on screen");
                return;
            }
            if (history.Players.Count == 0)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}history maps: history has no players");
                return;
            }
            var player = history.Players[0];
            var capturedHistory = history;
            var capturedPlayer = player;

            // Mid-run: opening the browser as an overlay while a live
            // NRun is still mounted produces NREs in NMapScreen.SetMap
            // (two NRuns fight for singletons). Prompt to abandon
            // upfront; on a non-cancel choice, tear the live run
            // down and run through the shared "transition to menu +
            // open browser" flow (same path the game-over banner
            // uses). The browser then lands cleanly on the
            // NRunHistory submenu state.
            if (RunManager.Instance?.IsInProgress == true)
            {
                RetryAbandonModal.Show(
                    title: "Abandon current run?",
                    body: "Viewing acts of another run requires ending your in-progress run.",
                    onChoice: c =>
                    {
                        if (c == RetryAbandonModal.Choice.Cancel) return;
                        RetryRunner.PerformAbandon(
                            writeHistory: c == RetryAbandonModal.Choice.AbandonSave,
                            inProgress: true);
                        _ = BrowserViaMainMenu.OpenAsync(capturedHistory, capturedPlayer);
                    });
                GD.Print($"{RetryMod.LogPrefix}view acts: mid-run abandon prompt shown for seed={history.Seed}");
                return;
            }

            // Menu case: defer to next idle frame — opening synchronously
            // inside the button's Pressed handler means we AddChild a
            // fullscreen Control while Godot is mid-way through
            // dispatching the click event, which leaves the GUI input
            // router unable to route subsequent events to the new
            // NMapScreen.
            Callable.From(() => NActMapBrowser.Open(capturedHistory, capturedPlayer)).CallDeferred();
            GD.Print($"{RetryMod.LogPrefix}opened act map browser for seed={history.Seed} (deferred)");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}open browser: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
