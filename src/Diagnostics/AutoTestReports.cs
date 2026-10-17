using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

internal static class AutoTestReports
{
    internal static void WriteComparisonReport(RunHistory history, RunHistoryPlayer historyPlayer, AutoTestConfig cfg, RunState state, RetryTarget target)
    {
        try
        {
            var report = BuildComparisonReport(history, historyPlayer, cfg.Act, cfg.Floor, state, target);
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

    internal static Godot.Collections.Dictionary BuildComparisonReport(RunHistory history, RunHistoryPlayer historyPlayer, int act, int floor, RunState state, RetryTarget target)
    {
        SerializablePlayer? expected = RngSnapshotStore.TryGetPlayerSnapshot(history.StartTime, history.Seed, act, floor, historyPlayer.Id);
        var live = state.Players.FirstOrDefault(p => p.NetId == historyPlayer.Id) ?? state.Players.First();
        SerializablePlayer actual = live.ToSerializable();
        string expectedPlayerHash = expected == null ? "missing" : RngSnapshotStore.Fingerprint(expected);
        string actualPlayerHash = RngSnapshotStore.Fingerprint(actual);
        string expectedMapHash = RngSnapshotStore.TryGetActMap(history.StartTime, history.Seed, act, out var savedMap) && savedMap != null ? FingerprintMap(savedMap) : "missing";
        string actualMapHash = state.Map == null ? "missing" : RngSnapshotStore.Fingerprint(state.Map);
        bool playerCoreMatches = expected != null && expected.CurrentHp == actual.CurrentHp && expected.MaxHp == actual.MaxHp && expected.Gold == actual.Gold && expected.Deck.Select(c => c.Id?.ToString()).SequenceEqual(actual.Deck.Select(c => c.Id?.ToString())) && expected.Relics.Select(r => r.Id?.ToString()).SequenceEqual(actual.Relics.Select(r => r.Id?.ToString())) && expected.Potions.Select(p => p.Id?.ToString()).SequenceEqual(actual.Potions.Select(p => p.Id?.ToString()));
        var expectedTypes = history.MapPointHistory[act].Take(floor + 1).Select(e => e.MapPointType.ToString()).ToList();
        var actualTypes = state.VisitedMapCoords.Select(coord => state.Map?.GetAllMapPoints().Append(state.Map.StartingMapPoint).FirstOrDefault(p => p.coord.row == coord.row && p.coord.col == coord.col)?.PointType.ToString() ?? "missing").ToList();
        return new Godot.Collections.Dictionary
        {
            ["passed"] = expectedMapHash == actualMapHash && playerCoreMatches && expectedTypes.SequenceEqual(actualTypes.Take(expectedTypes.Count)),
            ["player_core_passed"] = playerCoreMatches,
            ["player_packet_note"] = expectedPlayerHash == actualPlayerHash ? "exact" : "room entry advances transient player RNG/odds; core state is compared separately",
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

    internal static string FingerprintMap(SerializableActMap map)
    {
        var restored = new MegaCrit.Sts2.Core.Map.SavedActMap(map);
        return RngSnapshotStore.Fingerprint(restored);
    }

    internal static void FinishSequence(AutoTestHarness.SequenceState sequence)
    {
        try
        {
            var targets = new Godot.Collections.Array<string>();
            foreach (var target in sequence.Targets)
                targets.Add($"{target.Act}:{target.Floor}");
            var generated = new Godot.Collections.Array<string>();
            foreach (string historyFile in sequence.GeneratedHistoryFiles)
                generated.Add(historyFile);
            var report = new Godot.Collections.Dictionary
            {
                ["passed"] = sequence.AllPassed && sequence.Results.Count == sequence.Targets.Count,
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
            using var file = FileAccess.Open("user://retry_the_spire_e2e_sequence_report.json", FileAccess.ModeFlags.Write);
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
}
