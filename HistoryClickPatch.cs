// Hook into NMapPointHistoryEntry._Ready and wire a Released signal
// handler. NMapPointHistoryEntry inherits NClickableControl, so we
// just subscribe after the base class's ConnectSignals finishes.
// The selection + confirm flow lives in HistoryConfirmPanel; this
// patch only forwards the click.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;

namespace Retry;

[HarmonyPatch(typeof(NMapPointHistoryEntry), "_Ready")]
public static class NMapPointHistoryEntry_Ready_Patch
{
    static void Postfix(NMapPointHistoryEntry __instance)
    {
        if (!RetryMod.Enabled || __instance == null) return;
        try
        {
            // _Ready can fire again if the node is reused (e.g. on
            // player switch); tag once so we don't double-wire.
            if (__instance.HasMeta("retry_the_spire_click_wired")) return;
            __instance.SetMeta("retry_the_spire_click_wired", true);

            var captured = __instance;
            __instance.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NClickableControl>(_ => HistoryConfirmPanel.Select(captured)));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}wire history click: {ex.Message}");
        }
    }
}
