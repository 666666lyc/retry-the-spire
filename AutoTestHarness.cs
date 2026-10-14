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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

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
    public bool CompareAfterRetry;
    public bool ExactSecondPass;
    public bool AutoAbandonExisting;
    public bool WriteHistoryWhenAutoAbandon = true;
    public string SequenceTargets = "";
    public bool QuitWhenDone;
    public string BrowserClickFloors = "";
    public string ScreenshotPrefix = "retry_the_spire_browser";

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
                CompareAfterRetry = d.ContainsKey("compare_after_retry") && (bool)d["compare_after_retry"],
                ExactSecondPass = d.ContainsKey("exact_second_pass") && (bool)d["exact_second_pass"],
                AutoAbandonExisting = d.ContainsKey("auto_abandon_existing") && (bool)d["auto_abandon_existing"],
                WriteHistoryWhenAutoAbandon = !d.ContainsKey("write_history_when_auto_abandon")
                    || (bool)d["write_history_when_auto_abandon"],
                SequenceTargets = d.ContainsKey("sequence_targets") ? (string)d["sequence_targets"] : "",
                QuitWhenDone = d.ContainsKey("quit_when_done") && (bool)d["quit_when_done"],
                BrowserClickFloors = d.ContainsKey("browser_click_floors") ? (string)d["browser_click_floors"] : "",
                ScreenshotPrefix = d.ContainsKey("screenshot_prefix")
                    ? (string)d["screenshot_prefix"]
                    : "retry_the_spire_browser",
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
    private sealed class SequenceState
    {
        public required RunHistory SourceHistory;
        public required RunHistoryPlayer SourcePlayer;
        public required AutoTestConfig Config;
        public required List<(int Act, int Floor)> Targets;
        public readonly Godot.Collections.Array<Godot.Collections.Dictionary> Results = new();
        public readonly List<string> GeneratedHistoryFiles = new();
        public int Index;
        public bool AllPassed = true;
    }

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

            var sequenceTargets = ParseTargets(cfg.SequenceTargets);
            if (sequenceTargets.Count > 0)
            {
                RunSequence(history, player, cfg, sequenceTargets);
                return;
            }

            if (cfg.BrowserTest)
            {
                SpawnBrowserForTest(history, player, cfg);
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

            if (cfg.AutoAbandonExisting && RetryRunner.NeedsAbandonPrompt())
            {
                GD.Print($"{RetryMod.LogPrefix}auto-test: abandoning existing test run before launch");
                RetryRunner.PerformAbandon(writeHistory: cfg.WriteHistoryWhenAutoAbandon,
                    inProgress: RunManager.Instance?.IsInProgress == true);
            }

            // Dump the history's "ground truth" type sequence so the
            // debug-loop reader doesn't need to crack the .run file.
            DebugDump.DumpHistoryPath(history, cfg.Act, cfg.Floor);

            if (cfg.ExactSecondPass)
            {
                Action<RunState, RetryTarget>? observer = null;
                observer = (state, target) =>
                {
                    RetryRunner.RetryCompleted -= observer;
                    _ = PrepareExactSecondPass(state, target, cfg);
                };
                RetryRunner.RetryCompleted += observer;
            }
            else if (cfg.CompareAfterRetry)
            {
                Action<RunState, RetryTarget>? observer = null;
                observer = (state, target) =>
                {
                    RetryRunner.RetryCompleted -= observer;
                    WriteComparisonReport(history, player, cfg, state, target);
                };
                RetryRunner.RetryCompleted += observer;
            }

            if (cfg.AutoAbandonExisting)
                RetryRunner.BeginForAutomatedTest(history, player, cfg.Act, cfg.Floor, targetCoord: null);
            else
                RetryRunner.OnHistoryNodeClicked(history, entry, cfg.Floor, player);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test run: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static async System.Threading.Tasks.Task PrepareExactSecondPass(
        RunState state, RetryTarget target, AutoTestConfig cfg)
    {
        try
        {
            var console = new DevConsole(true);
            // Keep the golden snapshot's map untouched. Golden Compass calls
            // GenerateMap when obtained, which deliberately replaces the map
            // after the historical path has already been reconstructed and
            // therefore creates an internally inconsistent test fixture.
            foreach (string command in new[] { "gold 137", "heal 7" })
            {
                GD.Print($"{RetryMod.LogPrefix}auto-test native command starting: '{command}'");
                CmdResult result = console.ProcessCommand(command);
                if (result.task != null)
                {
                    var completed = await System.Threading.Tasks.Task.WhenAny(
                        result.task, System.Threading.Tasks.Task.Delay(3000));
                    if (completed != result.task)
                        GD.PrintErr($"{RetryMod.LogPrefix}auto-test native command timed out: '{command}'");
                }
                GD.Print($"{RetryMod.LogPrefix}auto-test native command '{command}': success={result.success} msg={result.msg}");
            }

            long startTime = RngSnapshotStore.GetCurrentRunStartTime();
            var coord = state.VisitedMapCoords.LastOrDefault();
            RngSnapshotStore.Capture(
                startTime, state.Rng.StringSeed, target.TargetActIndex,
                target.TargetFloorIndex, coord, state.Rng.ToSerializable().Counters,
                state.Players.Select(p => p.ToSerializable()).ToList(), state.Map);
            GD.Print($"{RetryMod.LogPrefix}auto-test: captured exact golden state startTime={startTime}");

            RetryRunner.PerformAbandon(writeHistory: true, inProgress: true);
            string fileName = startTime + ".run";
            var read = SaveManager.Instance.LoadRunHistory(fileName);
            if (!read.Success || read.SaveData == null)
                throw new InvalidOperationException($"second-pass history unavailable: {fileName} ({read.Status})");
            var history = read.SaveData;
            var player = history.Players.First(p => p.Id == target.Player.NetId);

            Action<RunState, RetryTarget>? compare = null;
            compare = (restored, retryTarget) =>
            {
                RetryRunner.RetryCompleted -= compare;
                WriteComparisonReport(history, player, cfg, restored, retryTarget);
            };
            RetryRunner.RetryCompleted += compare;
            RetryRunner.Begin(history, player, target.TargetActIndex, target.TargetFloorIndex, coord);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test exact second pass: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static List<(int Act, int Floor)> ParseTargets(string value)
    {
        var targets = new List<(int Act, int Floor)>();
        if (string.IsNullOrWhiteSpace(value)) return targets;

        foreach (string raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = raw.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], out int act)
                || !int.TryParse(parts[1], out int floor))
            {
                throw new InvalidOperationException(
                    $"invalid sequence target '{raw}', expected act:floor");
            }
            targets.Add((act, floor));
        }
        return targets;
    }

    private static void RunSequence(
        RunHistory history,
        RunHistoryPlayer player,
        AutoTestConfig cfg,
        List<(int Act, int Floor)> targets)
    {
        foreach (var (act, floor) in targets)
        {
            if (act < 0 || act >= history.MapPointHistory.Count
                || floor < 0 || floor >= history.MapPointHistory[act].Count)
            {
                throw new InvalidOperationException(
                    $"sequence target {act}:{floor} is outside source history");
            }
        }

        var sequence = new SequenceState
        {
            SourceHistory = history,
            SourcePlayer = player,
            Config = cfg,
            Targets = targets,
        };
        GD.Print($"{RetryMod.LogPrefix}auto-test sequence: {string.Join(",", targets.Select(t => $"{t.Act}:{t.Floor}"))}");
        LaunchSequenceTarget(sequence);
    }

    private static void LaunchSequenceTarget(SequenceState sequence)
    {
        try
        {
            if (sequence.Index >= sequence.Targets.Count)
            {
                FinishSequence(sequence);
                return;
            }

            var (act, floor) = sequence.Targets[sequence.Index];
            GD.Print($"{RetryMod.LogPrefix}auto-test sequence target {sequence.Index + 1}/{sequence.Targets.Count}: act={act} floor={floor}");
            DebugDump.DumpHistoryPath(sequence.SourceHistory, act, floor);

            if (RetryRunner.NeedsAbandonPrompt())
            {
                RetryRunner.PerformAbandon(
                    writeHistory: false,
                    inProgress: RunManager.Instance?.IsInProgress == true);
            }

            Action<RunState, RetryTarget>? firstPass = null;
            firstPass = (state, target) =>
            {
                RetryRunner.RetryCompleted -= firstPass;
                _ = PrepareSequenceSecondPass(sequence, state, target, act, floor);
            };
            RetryRunner.RetryCompleted += firstPass;
            RetryRunner.Begin(
                sequence.SourceHistory,
                sequence.SourcePlayer,
                act,
                floor,
                targetCoord: null);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test sequence launch: {ex.Message}\n{ex.StackTrace}");
            sequence.AllPassed = false;
            FinishSequence(sequence);
        }
    }

    private static async Task PrepareSequenceSecondPass(
        SequenceState sequence,
        RunState state,
        RetryTarget target,
        int act,
        int floor)
    {
        try
        {
            var console = new DevConsole(true);
            // These are native debug commands, but neither mutates the map.
            // Map-changing relic commands are unsuitable here because they
            // invalidate the already-recorded path before we write history.
            foreach (string command in new[] { "gold 137", "heal 7" })
            {
                GD.Print($"{RetryMod.LogPrefix}auto-test native command starting: '{command}'");
                CmdResult result = console.ProcessCommand(command);
                if (result.task != null)
                {
                    var completed = await Task.WhenAny(result.task, Task.Delay(3000));
                    if (completed != result.task)
                        GD.PrintErr($"{RetryMod.LogPrefix}auto-test native command timed out: '{command}'");
                }
                GD.Print($"{RetryMod.LogPrefix}auto-test native command '{command}': success={result.success} msg={result.msg}");
            }

            long startTime = RngSnapshotStore.GetCurrentRunStartTime();
            var coord = state.VisitedMapCoords.LastOrDefault();
            RngSnapshotStore.Capture(
                startTime,
                state.Rng.StringSeed,
                act,
                floor,
                coord,
                state.Rng.ToSerializable().Counters,
                state.Players.Select(p => p.ToSerializable()).ToList(),
                state.Map);
            GD.Print($"{RetryMod.LogPrefix}auto-test sequence: captured exact golden state startTime={startTime}");

            RetryRunner.PerformAbandon(writeHistory: true, inProgress: true);
            string fileName = startTime + ".run";
            sequence.GeneratedHistoryFiles.Add(fileName);
            var read = SaveManager.Instance.LoadRunHistory(fileName);
            if (!read.Success || read.SaveData == null)
                throw new InvalidOperationException($"second-pass history unavailable: {fileName} ({read.Status})");

            RunHistory exactHistory = read.SaveData;
            RunHistoryPlayer exactPlayer = exactHistory.Players.First(p => p.Id == target.Player.NetId);
            Action<RunState, RetryTarget>? secondPass = null;
            secondPass = (restored, retryTarget) =>
            {
                RetryRunner.RetryCompleted -= secondPass;
                try
                {
                    var report = BuildComparisonReport(
                        exactHistory,
                        exactPlayer,
                        act,
                        floor,
                        restored,
                        retryTarget);
                    report["sequence_index"] = sequence.Index;
                    report["generated_history_file"] = fileName;
                    sequence.Results.Add(report);
                    bool passed = report.ContainsKey("passed") && (bool)report["passed"];
                    sequence.AllPassed &= passed;
                    GD.Print($"{RetryMod.LogPrefix}E2E_SEQUENCE_ITEM {Json.Stringify(report)}");
                }
                catch (Exception ex)
                {
                    sequence.AllPassed = false;
                    GD.PrintErr($"{RetryMod.LogPrefix}auto-test sequence compare: {ex.Message}\n{ex.StackTrace}");
                }

                RetryRunner.PerformAbandon(writeHistory: false, inProgress: true);
                sequence.Index++;
                ((SceneTree)Engine.GetMainLoop()).CreateTimer(0.35).Connect(
                    "timeout", Callable.From(() => LaunchSequenceTarget(sequence)));
            };
            RetryRunner.RetryCompleted += secondPass;
            RetryRunner.Begin(exactHistory, exactPlayer, act, floor, coord);
        }
        catch (Exception ex)
        {
            sequence.AllPassed = false;
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test sequence second pass: {ex.Message}\n{ex.StackTrace}");
            FinishSequence(sequence);
        }
    }

    private static void FinishSequence(SequenceState sequence)
    {
        try
        {
            var targets = new Godot.Collections.Array<string>();
            foreach (var target in sequence.Targets) targets.Add($"{target.Act}:{target.Floor}");
            var generated = new Godot.Collections.Array<string>();
            foreach (string historyFile in sequence.GeneratedHistoryFiles) generated.Add(historyFile);

            var report = new Godot.Collections.Dictionary
            {
                ["passed"] = sequence.AllPassed
                    && sequence.Results.Count == sequence.Targets.Count,
                ["source_history_file"] = sequence.Config.RunFileBasename,
                ["source_start_time"] = sequence.SourceHistory.StartTime,
                ["seed"] = sequence.SourceHistory.Seed,
                ["character"] = sequence.SourcePlayer.Character.ToString(),
                ["target_count"] = sequence.Targets.Count,
                ["completed_count"] = sequence.Results.Count,
                ["targets"] = targets,
                ["generated_history_files"] = generated,
                ["results"] = sequence.Results,
                ["sidecar_encoded_bytes"] = RngSnapshotStore.GetEncodedSizeForDiagnostics(),
            };
            string json = Json.Stringify(report, "  ");
            using var file = FileAccess.Open(
                "user://retry_the_spire_e2e_sequence_report.json",
                FileAccess.ModeFlags.Write);
            file?.StoreString(json);
            GD.Print($"{RetryMod.LogPrefix}E2E_SEQUENCE_DONE {json}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test sequence finish: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            if (sequence.Config.QuitWhenDone)
                ((SceneTree)Engine.GetMainLoop()).Quit();
        }
    }

    private static void WriteComparisonReport(
        RunHistory history,
        RunHistoryPlayer historyPlayer,
        AutoTestConfig cfg,
        RunState state,
        RetryTarget target)
    {
        try
        {
            var report = BuildComparisonReport(
                history, historyPlayer, cfg.Act, cfg.Floor, state, target);
            string json = Json.Stringify(report, "  ");
            using var file = FileAccess.Open("user://retry_the_spire_e2e_report.json", FileAccess.ModeFlags.Write);
            file?.StoreString(json);
            GD.Print($"{RetryMod.LogPrefix}E2E_COMPARE {json}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test comparison: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static Godot.Collections.Dictionary BuildComparisonReport(
        RunHistory history,
        RunHistoryPlayer historyPlayer,
        int act,
        int floor,
        RunState state,
        RetryTarget target)
    {
        SerializablePlayer? expected = RngSnapshotStore.TryGetPlayerSnapshot(
            history.StartTime, history.Seed, act, floor, historyPlayer.Id);
        var live = state.Players.FirstOrDefault(p => p.NetId == historyPlayer.Id)
            ?? state.Players.First();
        SerializablePlayer actual = live.ToSerializable();
        string expectedPlayerHash = expected == null ? "missing" : RngSnapshotStore.Fingerprint(expected);
        string actualPlayerHash = RngSnapshotStore.Fingerprint(actual);
        string expectedMapHash = RngSnapshotStore.TryGetActMap(
            history.StartTime, history.Seed, act, out var savedMap) && savedMap != null
            ? FingerprintMap(savedMap)
            : "missing";
        string actualMapHash = state.Map == null ? "missing" : RngSnapshotStore.Fingerprint(state.Map);
        bool playerCoreMatches = expected != null
            && expected.CurrentHp == actual.CurrentHp
            && expected.MaxHp == actual.MaxHp
            && expected.Gold == actual.Gold
            && expected.Deck.Select(c => c.Id?.ToString()).SequenceEqual(actual.Deck.Select(c => c.Id?.ToString()))
            && expected.Relics.Select(r => r.Id?.ToString()).SequenceEqual(actual.Relics.Select(r => r.Id?.ToString()))
            && expected.Potions.Select(p => p.Id?.ToString()).SequenceEqual(actual.Potions.Select(p => p.Id?.ToString()));

        var expectedTypes = history.MapPointHistory[act]
            .Take(floor + 1).Select(e => e.MapPointType.ToString()).ToList();
        var actualTypes = state.VisitedMapCoords.Select(coord => state.Map?.GetAllMapPoints()
                .Append(state.Map.StartingMapPoint)
                .FirstOrDefault(p => p.coord.row == coord.row && p.coord.col == coord.col)?.PointType.ToString()
                ?? "missing").ToList();

        return new Godot.Collections.Dictionary
        {
            ["passed"] = expectedMapHash == actualMapHash
                && playerCoreMatches
                && expectedTypes.SequenceEqual(actualTypes.Take(expectedTypes.Count)),
            ["player_core_passed"] = playerCoreMatches,
            ["player_packet_note"] = expectedPlayerHash == actualPlayerHash
                ? "exact"
                : "room entry advances transient player RNG/odds; core state is compared separately",
            ["seed"] = history.Seed,
            ["source_start_time"] = history.StartTime,
            ["act"] = act,
            ["floor"] = floor,
            ["map_snapshot_present"] = expectedMapHash != "missing",
            ["expected_map_sha256"] = expectedMapHash,
            ["actual_map_sha256"] = actualMapHash,
            ["expected_player_sha256"] = expectedPlayerHash,
            ["actual_player_sha256"] = actualPlayerHash,
            ["expected_hp"] = expected?.CurrentHp ?? -1,
            ["actual_hp"] = actual.CurrentHp,
            ["expected_max_hp"] = expected?.MaxHp ?? -1,
            ["actual_max_hp"] = actual.MaxHp,
            ["expected_gold"] = expected?.Gold ?? -1,
            ["actual_gold"] = actual.Gold,
            ["expected_deck"] = expected == null ? "missing" : string.Join(",", expected.Deck.Select(c => c.Id?.ToString() ?? "?")),
            ["actual_deck"] = string.Join(",", actual.Deck.Select(c => c.Id?.ToString() ?? "?")),
            ["expected_relics"] = expected == null ? "missing" : string.Join(",", expected.Relics.Select(r => r.Id?.ToString() ?? "?")),
            ["actual_relics"] = string.Join(",", actual.Relics.Select(r => r.Id?.ToString() ?? "?")),
            ["expected_potions"] = expected == null ? "missing" : string.Join(",", expected.Potions.Select(p => p.Id?.ToString() ?? "?")),
            ["actual_potions"] = string.Join(",", actual.Potions.Select(p => p.Id?.ToString() ?? "?")),
            ["expected_path_types"] = string.Join(",", expectedTypes),
            ["actual_path_types"] = string.Join(",", actualTypes),
            ["sidecar_encoded_bytes"] = RngSnapshotStore.GetEncodedSizeForDiagnostics(),
        };
    }

    private static void SpawnBrowserForTest(
        RunHistory history,
        RunHistoryPlayer player,
        AutoTestConfig cfg)
    {
        var floors = new List<int>();
        foreach (string raw in cfg.BrowserClickFloors.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(raw.Trim(), out int floor))
                throw new InvalidOperationException($"invalid browser floor '{raw}'");
            floors.Add(floor);
        }

        if (floors.Count == 0)
        {
            SpawnBrowser(history, player);
            return;
        }

        Action? ready = null;
        ready = () =>
        {
            NActMapBrowser.ReadyForAutomation -= ready;
            _ = CaptureBrowserClicks(history, cfg, floors);
        };
        NActMapBrowser.ReadyForAutomation += ready;
        SpawnBrowser(history, player);
    }

    private static async Task CaptureBrowserClicks(
        RunHistory history,
        AutoTestConfig cfg,
        List<int> floors)
    {
        var captures = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        try
        {
            foreach (int floor in floors)
            {
                MapCoord? coord = RngSnapshotStore.TryGetExactCoord(
                    history.StartTime, history.Seed, cfg.Act, floor);
                if (coord == null)
                {
                    captures.Add(new Godot.Collections.Dictionary
                    {
                        ["act"] = cfg.Act,
                        ["floor"] = floor,
                        ["passed"] = false,
                        ["error"] = "exact coordinate missing",
                    });
                    continue;
                }

                NActMapBrowser.HandleMapPointClick(coord.Value);
                await ((SceneTree)Engine.GetMainLoop()).ToSignal(
                    ((SceneTree)Engine.GetMainLoop()).CreateTimer(0.45), "timeout");

                string fileName = $"{cfg.ScreenshotPrefix}_act{cfg.Act}_floor{floor}.png";
                string userPath = "user://" + fileName;
                Error saveError = ((SceneTree)Engine.GetMainLoop()).Root
                    .GetTexture().GetImage().SavePng(userPath);
                captures.Add(new Godot.Collections.Dictionary
                {
                    ["act"] = cfg.Act,
                    ["floor"] = floor,
                    ["row"] = coord.Value.row,
                    ["col"] = coord.Value.col,
                    ["passed"] = saveError == Error.Ok,
                    ["screenshot"] = fileName,
                    ["save_error"] = saveError.ToString(),
                });
                GD.Print($"{RetryMod.LogPrefix}browser capture: act={cfg.Act} floor={floor} coord={coord} file={fileName} error={saveError}");
            }

            var report = new Godot.Collections.Dictionary
            {
                ["passed"] = captures.Count == floors.Count
                    && captures.All(item => item.ContainsKey("passed") && (bool)item["passed"]),
                ["history_file"] = cfg.RunFileBasename,
                ["seed"] = history.Seed,
                ["act"] = cfg.Act,
                ["captures"] = captures,
            };
            using var file = FileAccess.Open(
                "user://retry_the_spire_browser_report.json",
                FileAccess.ModeFlags.Write);
            file?.StoreString(Json.Stringify(report, "  "));
            GD.Print($"{RetryMod.LogPrefix}BROWSER_CAPTURE_DONE {Json.Stringify(report)}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}browser capture: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            if (cfg.QuitWhenDone)
                ((SceneTree)Engine.GetMainLoop()).Quit();
        }
    }

    private static string FingerprintMap(SerializableActMap map)
    {
        var restored = new MegaCrit.Sts2.Core.Map.SavedActMap(map);
        return RngSnapshotStore.Fingerprint(restored);
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
