// Coordinates historical retry requests and save-slot-specific abandonment.
// Single-player reconstructs a seeded run, restores its player state, then
// navigates to the historical room. Multiplayer hosting uses a native save
// and the game's load-run lobby through MultiplayerRetryLauncher.
using System;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;

namespace Retry;

public static class RetryRunner
{
    public static void PerformAbandon(bool writeHistory, bool inProgress) => RunAbandonment.PerformAbandon(writeHistory, inProgress);
    public static void PerformMultiplayerAbandon(bool writeHistory, ulong localPlayerId) => RunAbandonment.PerformMultiplayerAbandon(writeHistory, localPlayerId);
    internal static void OnRetryCompleted(RunState state, RetryTarget target) => RetryCompleted?.Invoke(state, target);
    public static event Action<RunState, RetryTarget>? RetryCompleted;
    // History-list clicks open the map browser for multiplayer; single-player
    // retains the direct act-start retry route.
    public static void OnHistoryNodeClicked(RunHistory history, MapPointHistoryEntry entry, int floor, RunHistoryPlayer? player)
    {
        if (player == null)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}history click: no player");
            return;
        }

        var(actIndex, floorIndex) = HistoryTargetBuilder.LocateInHistory(history, entry);
        if (actIndex < 0)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}history click: entry not found in history");
            return;
        }

        GD.Print($"{RetryMod.LogPrefix}history click resolved to act={actIndex} floor={floorIndex} seed={history.Seed}");
        if (history.Players.Count > 1)
        {
            // A multiplayer history must retain every recorded player and use
            // the native multiplayer save slot. The single-player new-run
            // pipeline below intentionally reconstructs only one selected
            // player and would otherwise overwrite current_run.save.
            NActMapBrowser.Open(history, player);
            return;
        }

        // No coordinate: the single-player history route starts at act entry.
        Begin(history, player, actIndex, floorIndex, targetCoord: null);
    }

    // Public entry used by the act-map browser (and the direct history click
    // path above). Snapshots state at the (actIndex, floorIndex)
    // boundary and launches.
    public static void Begin(RunHistory history, RunHistoryPlayer player, int actIndex, int floorIndex, MapCoord? targetCoord)
    {
        // Either condition triggers our 3-option modal: in-progress
        // (mid-run retry) or a saved run on disk (would clobber).
        // Cancel keeps state; AbandonSave writes a run_history entry
        // and deletes the save; AbandonNoSave skips the history write
        // (avoids flooding history with quick test retries) but still
        // tears the run down.
        try
        {
            bool inProgress = RunManager.Instance?.IsInProgress == true;
            bool hasSave = SaveManager.Instance?.HasRunSave == true;
            if (inProgress || hasSave)
            {
                string title = inProgress ? HistoryRetryText.AbandonCurrentRunTitle : HistoryRetryText.AbandonSavedRunTitle;
                string body = inProgress ? HistoryRetryText.RetryEndsCurrentRunBody : HistoryRetryText.RetryOverwritesSavedRunBody;
                RetryAbandonModal.Show(title, body, choice =>
                {
                    switch (choice)
                    {
                        case RetryAbandonModal.Choice.Cancel:
                            GD.Print($"{RetryMod.LogPrefix}retry cancelled by user");
                            return;
                        case RetryAbandonModal.Choice.AbandonSave:
                            RunAbandonment.PerformAbandon(writeHistory: true, inProgress: inProgress);
                            BeginInternal(history, player, actIndex, floorIndex, targetCoord);
                            break;
                        case RetryAbandonModal.Choice.AbandonNoSave:
                            RunAbandonment.PerformAbandon(writeHistory: false, inProgress: inProgress);
                            BeginInternal(history, player, actIndex, floorIndex, targetCoord);
                            break;
                    }
                });
                return;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}Begin guard: {ex.Message}");
        }

        BeginInternal(history, player, actIndex, floorIndex, targetCoord);
    }

    public static bool NeedsAbandonPrompt()
    {
        try
        {
            return RunManager.Instance?.IsInProgress == true || SaveManager.Instance?.HasRunSave == true;
        }
        catch
        {
            return false;
        }
    }

    // Deterministic harness entry: performs the same native abandon path as
    // the confirmation popup, but makes the check and launch contiguous so
    // startup cloud-sync cannot surface a save between two separate calls.
    public static void BeginForAutomatedTest(RunHistory history, RunHistoryPlayer player, int actIndex, int floorIndex, MapCoord? targetCoord)
    {
        bool inProgress = RunManager.Instance?.IsInProgress == true;
        if (inProgress || SaveManager.Instance?.HasRunSave == true)
            RunAbandonment.PerformAbandon(writeHistory: true, inProgress: inProgress);
        BeginInternal(history, player, actIndex, floorIndex, targetCoord);
    }

    internal static void BeginInternal(RunHistory history, RunHistoryPlayer player, int actIndex, int floorIndex, MapCoord? targetCoord)
    {
        try
        {
            // Defensive: bail clearly if the run record is broken
            // rather than crashing inside LoadRun.
            if (history == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}Begin: history null");
                return;
            }

            if (string.IsNullOrEmpty(history.Seed))
            {
                GD.PrintErr($"{RetryMod.LogPrefix}Begin: history has no seed");
                return;
            }

            if (history.Acts == null || history.Acts.Count == 0)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}Begin: history has no acts");
                return;
            }

            if (actIndex < 0 || actIndex >= history.Acts.Count)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}Begin: actIndex {actIndex} out of range");
                return;
            }

            if (ModelDb.GetByIdOrNull<CharacterModel>(player.Character) == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}Begin: character {player.Character} not found in current ModelDb — was this run on a different version / with different mods?");
                return;
            }

            var target = HistoryTargetBuilder.Build(history, player, actIndex, floorIndex);
            var snapshot = target.Player;
            GD.Print($"{RetryMod.LogPrefix}Begin: seed={target.Seed} act={actIndex} floor={floorIndex} char={player.Character} deck={snapshot.Deck.Count} relics={snapshot.Relics.Count} hp={snapshot.CurrentHp}/{snapshot.MaxHp} gold={snapshot.Gold}");
            _ = SingleplayerHistoryLauncher.LaunchAsync(target);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}Begin: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
