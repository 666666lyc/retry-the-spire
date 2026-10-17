using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace Retry;

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
public static class NMainMenu_Ready_Patch
{
    // Static guard — once the harness has fired in this game process
    // we don't fire again on subsequent menu visits (e.g. after
    // abandoning the retry the player gets booted back to the menu).
    private static bool _fired;
    static void Postfix(NMainMenu __instance)
    {
        if (_fired)
            return;
        if (!RetryMod.Enabled)
            return;
        try
        {
            var cfg = AutoTestConfig.LoadOrNull();
            if (cfg == null || !cfg.Enabled)
                return;
            _fired = true;
            GD.Print($"{RetryMod.LogPrefix}auto-test: triggering retry → file={cfg.RunFileBasename} act={cfg.Act} floor={cfg.Floor}");
            __instance.GetTree().CreateTimer(1.5).Connect("timeout", Callable.From(() => AutoTestHarness.Run(cfg)));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test schedule: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
