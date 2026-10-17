using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Retry;

public static class AutoTestHarness
{
    internal sealed class SequenceState
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
                RetryRunner.PerformAbandon(writeHistory: cfg.WriteHistoryWhenAutoAbandon, inProgress: RunManager.Instance?.IsInProgress == true);
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
                    AutoTestReports.WriteComparisonReport(history, player, cfg, state, target);
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

    internal static async System.Threading.Tasks.Task PrepareExactSecondPass(RunState state, RetryTarget target, AutoTestConfig cfg)
    {
        try
        {
            var console = new DevConsole(true);
            // Keep the golden snapshot's map untouched. Golden Compass calls
            // GenerateMap when obtained, which deliberately replaces the map
            // after the historical path has already been reconstructed and
            // therefore creates an internally inconsistent test fixture.
            foreach (string command in new[]
            {
                "gold 137",
                "heal 7"
            }

            )
            {
                GD.Print($"{RetryMod.LogPrefix}auto-test native command starting: '{command}'");
                CmdResult result = console.ProcessCommand(command);
                if (result.task != null)
                {
                    var completed = await System.Threading.Tasks.Task.WhenAny(result.task, System.Threading.Tasks.Task.Delay(3000));
                    if (completed != result.task)
                        GD.PrintErr($"{RetryMod.LogPrefix}auto-test native command timed out: '{command}'");
                }

                GD.Print($"{RetryMod.LogPrefix}auto-test native command '{command}': success={result.success} msg={result.msg}");
            }

            long startTime = RngSnapshotStore.GetCurrentRunStartTime();
            var coord = state.VisitedMapCoords.LastOrDefault();
            RngSnapshotStore.Capture(startTime, state.Rng.StringSeed, target.TargetActIndex, target.TargetFloorIndex, coord, state.Rng.ToSerializable().Counters, state.Players.Select(p => p.ToSerializable()).ToList(), state.Map);
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
                AutoTestReports.WriteComparisonReport(history, player, cfg, restored, retryTarget);
            };
            RetryRunner.RetryCompleted += compare;
            RetryRunner.Begin(history, player, target.TargetActIndex, target.TargetFloorIndex, coord);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test exact second pass: {ex.Message}\n{ex.StackTrace}");
        }
    }

    internal static List<(int Act, int Floor)> ParseTargets(string value)
    {
        var targets = new List<(int Act, int Floor)>();
        if (string.IsNullOrWhiteSpace(value))
            return targets;
        foreach (string raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = raw.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int act) || !int.TryParse(parts[1], out int floor))
            {
                throw new InvalidOperationException($"invalid sequence target '{raw}', expected act:floor");
            }

            targets.Add((act, floor));
        }

        return targets;
    }

    internal static void RunSequence(RunHistory history, RunHistoryPlayer player, AutoTestConfig cfg, List<(int Act, int Floor)> targets)
    {
        foreach (var(act, floor)in targets)
        {
            if (act < 0 || act >= history.MapPointHistory.Count || floor < 0 || floor >= history.MapPointHistory[act].Count)
            {
                throw new InvalidOperationException($"sequence target {act}:{floor} is outside source history");
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

    internal static void LaunchSequenceTarget(SequenceState sequence)
    {
        try
        {
            if (sequence.Index >= sequence.Targets.Count)
            {
                AutoTestReports.FinishSequence(sequence);
                return;
            }

            var(act, floor) = sequence.Targets[sequence.Index];
            GD.Print($"{RetryMod.LogPrefix}auto-test sequence target {sequence.Index + 1}/{sequence.Targets.Count}: act={act} floor={floor}");
            DebugDump.DumpHistoryPath(sequence.SourceHistory, act, floor);
            if (RetryRunner.NeedsAbandonPrompt())
            {
                RetryRunner.PerformAbandon(writeHistory: false, inProgress: RunManager.Instance?.IsInProgress == true);
            }

            Action<RunState, RetryTarget>? firstPass = null;
            firstPass = (state, target) =>
            {
                RetryRunner.RetryCompleted -= firstPass;
                _ = PrepareSequenceSecondPass(sequence, state, target, act, floor);
            };
            RetryRunner.RetryCompleted += firstPass;
            RetryRunner.Begin(sequence.SourceHistory, sequence.SourcePlayer, act, floor, targetCoord: null);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test sequence launch: {ex.Message}\n{ex.StackTrace}");
            sequence.AllPassed = false;
            AutoTestReports.FinishSequence(sequence);
        }
    }

    internal static async Task PrepareSequenceSecondPass(SequenceState sequence, RunState state, RetryTarget target, int act, int floor)
    {
        try
        {
            var console = new DevConsole(true);
            // These are native debug commands, but neither mutates the map.
            // Map-changing relic commands are unsuitable here because they
            // invalidate the already-recorded path before we write history.
            foreach (string command in new[]
            {
                "gold 137",
                "heal 7"
            }

            )
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
            RngSnapshotStore.Capture(startTime, state.Rng.StringSeed, act, floor, coord, state.Rng.ToSerializable().Counters, state.Players.Select(p => p.ToSerializable()).ToList(), state.Map);
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
                    var report = AutoTestReports.BuildComparisonReport(exactHistory, exactPlayer, act, floor, restored, retryTarget);
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
                ((SceneTree)Engine.GetMainLoop()).CreateTimer(0.35).Connect("timeout", Callable.From(() => LaunchSequenceTarget(sequence)));
            };
            RetryRunner.RetryCompleted += secondPass;
            RetryRunner.Begin(exactHistory, exactPlayer, act, floor, coord);
        }
        catch (Exception ex)
        {
            sequence.AllPassed = false;
            GD.PrintErr($"{RetryMod.LogPrefix}auto-test sequence second pass: {ex.Message}\n{ex.StackTrace}");
            AutoTestReports.FinishSequence(sequence);
        }
    }

    internal static void SpawnBrowserForTest(RunHistory history, RunHistoryPlayer player, AutoTestConfig cfg)
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

    internal static async Task CaptureBrowserClicks(RunHistory history, AutoTestConfig cfg, List<int> floors)
    {
        var captures = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        try
        {
            foreach (int floor in floors)
            {
                MapCoord? coord = RngSnapshotStore.TryGetExactCoord(history.StartTime, history.Seed, cfg.Act, floor);
                if (coord == null)
                {
                    captures.Add(new Godot.Collections.Dictionary { ["act"] = cfg.Act, ["floor"] = floor, ["passed"] = false, ["error"] = "exact coordinate missing", });
                    continue;
                }

                NActMapBrowser.HandleMapPointClick(coord.Value);
                await ((SceneTree)Engine.GetMainLoop()).ToSignal(((SceneTree)Engine.GetMainLoop()).CreateTimer(0.45), "timeout");
                string fileName = $"{cfg.ScreenshotPrefix}_act{cfg.Act}_floor{floor}.png";
                string userPath = "user://" + fileName;
                Error saveError = ((SceneTree)Engine.GetMainLoop()).Root.GetTexture().GetImage().SavePng(userPath);
                captures.Add(new Godot.Collections.Dictionary { ["act"] = cfg.Act, ["floor"] = floor, ["row"] = coord.Value.row, ["col"] = coord.Value.col, ["passed"] = saveError == Error.Ok, ["screenshot"] = fileName, ["save_error"] = saveError.ToString(), });
                GD.Print($"{RetryMod.LogPrefix}browser capture: act={cfg.Act} floor={floor} coord={coord} file={fileName} error={saveError}");
            }

            var report = new Godot.Collections.Dictionary
            {
                ["passed"] = captures.Count == floors.Count && captures.All(item => item.ContainsKey("passed") && (bool)item["passed"]),
                ["history_file"] = cfg.RunFileBasename,
                ["seed"] = history.Seed,
                ["act"] = cfg.Act,
                ["captures"] = captures,
            };
            using var file = FileAccess.Open("user://retry_the_spire_browser_report.json", FileAccess.ModeFlags.Write);
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

    // Spawn the act-map browser. Calls NActMapBrowser.Open which uses
    // the canonical NRun load flow (full UI: TopBar, MapBg, MapScreen).
    internal static void SpawnBrowser(RunHistory history, RunHistoryPlayer player)
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

    internal static void ProbeMapScreenScene()
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
