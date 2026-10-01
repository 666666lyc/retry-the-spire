// Auto-triggers a retry on main menu load — lets the debug loop
// iterate without manual clicking. Reads a small JSON config from
// the game's user data dir:
//
//   user://retry_the_spire_test_target.json
//   { "enabled": true, "run_file_basename": "1779070640.run",
//     "act": 0, "floor": 5 }
//
// When `enabled` is true, NMainMenu._Ready Postfix loads the named
// run history file and calls RetryRunner.Begin with the recorded
// player + act/floor target. Set `enabled` to false (or delete the
// file) to disable the harness.
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

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
        if (_fired) return;
        if (!RetryMod.Enabled) return;
        try
        {
            var cfg = AutoTestConfig.LoadOrNull();
            if (cfg == null || !cfg.Enabled) return;
            _fired = true;
            GD.Print($"{RetryMod.LogPrefix}auto-test: triggering retry → file={cfg.RunFileBasename} act={cfg.Act} floor={cfg.Floor}");
            __instance.GetTree().CreateTimer(1.5).Connect("timeout",
                Callable.From(() => AutoTestHarness.Run(cfg)));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test schedule: {ex.Message}\n{ex.StackTrace}");
        }
    }
}

public sealed class AutoTestConfig
{
    public bool Enabled;
    public string RunFileBasename = "";
    public int Act;
    public int Floor;
    public bool BrowserTest;

    public static AutoTestConfig? LoadOrNull()
    {
        try
        {
            using var f = FileAccess.Open("user://retry_the_spire_test_target.json", FileAccess.ModeFlags.Read);
            if (f == null) return null;
            var json = f.GetAsText();
            var parser = new Json();
            if (parser.Parse(json) != Error.Ok) return null;
            if (parser.Data.AsGodotDictionary() is not Godot.Collections.Dictionary d) return null;
            var cfg = new AutoTestConfig
            {
                Enabled = d.ContainsKey("enabled") && (bool)d["enabled"],
                RunFileBasename = d.ContainsKey("run_file_basename") ? (string)d["run_file_basename"] : "",
                Act = d.ContainsKey("act") ? (int)d["act"] : 0,
                Floor = d.ContainsKey("floor") ? (int)d["floor"] : 0,
                BrowserTest = d.ContainsKey("browser_test") && (bool)d["browser_test"],
            };
            return cfg;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test config: {ex.Message}");
            return null;
        }
    }
}

public static class AutoTestHarness
{
    public static void Run(AutoTestConfig cfg)
    {
        try
        {
            var read = SaveManager.Instance.LoadRunHistory(cfg.RunFileBasename);
            if (!read.Success || read.SaveData == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}auto-test load failed: {read.Status} ({cfg.RunFileBasename})");
                return;
            }
            var history = read.SaveData;
            if (history.Players.Count == 0)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}auto-test: history has no players");
                return;
            }
            var player = history.Players[0];
            GD.Print($"{RetryMod.LogPrefix}auto-test: loaded history seed={history.Seed} acts={history.Acts.Count} floors[0]={history.MapPointHistory[0].Count}");

            if (cfg.BrowserTest)
            {
                SpawnBrowser(history, player);
                return;
            }

            int actCount = history.MapPointHistory.Count;
            if (cfg.Act < 0 || cfg.Act >= actCount)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}auto-test: act {cfg.Act} out of range ({actCount})");
                return;
            }
            var actHistory = history.MapPointHistory[cfg.Act];
            int floorCount = actHistory.Count;
            if (cfg.Floor < 0 || cfg.Floor >= floorCount)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}auto-test: floor {cfg.Floor} out of range (act has {floorCount})");
                return;
            }
            var entry = actHistory[cfg.Floor];

            // Dump the history's "ground truth" type sequence so the
            // debug-loop reader doesn't need to crack the .run file.
            DebugDump.DumpHistoryPath(history, cfg.Act, cfg.Floor);

            RetryRunner.OnHistoryNodeClicked(history, entry, cfg.Floor, player);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test run: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Spawn the act-map browser. Calls NActMapBrowser.Open which uses
    // the canonical NRun load flow (full UI: TopBar, MapBg, MapScreen).
    private static void SpawnBrowser(RunHistory history, RunHistoryPlayer player)
    {
        try
        {
            NActMapBrowser.Open(history, player);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test browser: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void ProbeMapScreenScene()
    {
        string[] candidates = new[]
        {
            "res://scenes/screens/map/map_screen.tscn",
            "res://scenes/ui/map_screen.tscn",
            "res://scenes/screens/map_screen.tscn",
            "res://scenes/screens/map/n_map_screen.tscn",
            "res://scenes/global_ui.tscn",
            "res://scenes/ui/global_ui.tscn",
            "res://scenes/run.tscn",
            "res://scenes/screens/map/map.tscn",
        };
        foreach (var p in candidates)
        {
            bool exists = Godot.ResourceLoader.Exists(p);
            GD.Print($"{RetryMod.LogPrefix}probe scene: {p} exists={exists}");
        }
    }
}
