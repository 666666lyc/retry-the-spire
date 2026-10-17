using System;
using Godot;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Retry;

internal static class RunAbandonment
{
    // Tear down the current/saved run. When writeHistory is true we
    // mirror NMainMenu.AbandonRun: UpdateProgressWithRunData + a
    // RunHistoryUtilities.CreateRunHistoryEntry stamped as abandoned.
    // When false (Choice.AbandonNoSave) we skip the history write to
    // keep test-retry sessions from spamming the run history list.
    public static void PerformAbandon(bool writeHistory, bool inProgress)
    {
        try
        {
            SerializableRun? save = null;
            if (inProgress)
            {
                try
                {
                    save = RunManager.Instance?.ToSave(preFinishedRoom: null);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}abandon ToSave: {ex.Message}");
                }
            }
            else
            {
                var read = SaveManager.Instance?.LoadRunSave();
                if (read != null && read.Success)
                    save = read.SaveData;
            }

            if (writeHistory && save != null)
            {
                try
                {
                    SaveManager.Instance.UpdateProgressWithRunData(save, victory: false);
                    MegaCrit.Sts2.Core.Runs.RunHistoryUtilities.CreateRunHistoryEntry(save, victory: false, isAbandoned: true, save.PlatformType);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}abandon write history: {ex.Message}");
                }
            }

            if (inProgress)
            {
                // graceful: true so CombatManager.Reset actually nulls
                // its _state. With graceful: false the cleanup block
                // (Reset graceful=true path, line 638 of CombatManager)
                // is skipped, leaving stale combat state — the next
                // run's combat entry then throws "Make sure to reset
                // the combat before setting up a new one." in
                // SetUpCombat. Non-combat retry targets (event /
                // campfire / treasure) don't hit SetUpCombat so the
                // bug only manifested on combat retries.
                try
                {
                    RunManager.Instance.CleanUp(graceful: true);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}abandon CleanUp: {ex.Message}");
                }
            }

            try
            {
                SaveManager.Instance?.DeleteCurrentRun();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}abandon DeleteCurrentRun: {ex.Message}");
            }

            GD.Print($"{RetryMod.LogPrefix}abandon: writeHistory={writeHistory} inProgress={inProgress}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}PerformAbandon: {ex.Message}");
        }
    }

    // Multiplayer uses a separate native save slot
    // (current_run_mp.save). Never route this through PerformAbandon:
    // that method deliberately targets current_run.save and would erase
    // an unrelated single-player run. This mirrors the game's own
    // NMultiplayerSubmenu.TryAbandonMultiplayerRun flow.
    public static void PerformMultiplayerAbandon(bool writeHistory, ulong localPlayerId)
    {
        try
        {
            SerializableRun? save = null;
            var read = SaveManager.Instance?.LoadAndCanonicalizeMultiplayerRunSave(localPlayerId);
            if (read != null && read.Success)
                save = read.SaveData;
            if (writeHistory && save != null)
            {
                try
                {
                    SaveManager.Instance.UpdateProgressWithRunData(save, victory: false);
                    MegaCrit.Sts2.Core.Runs.RunHistoryUtilities.CreateRunHistoryEntry(save, victory: false, isAbandoned: true, save.PlatformType);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}multiplayer abandon write history: {ex.Message}");
                }
            }

            try
            {
                SaveManager.Instance?.DeleteCurrentMultiplayerRun();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}abandon DeleteCurrentMultiplayerRun: {ex.Message}");
            }

            GD.Print($"{RetryMod.LogPrefix}multiplayer abandon: " + $"writeHistory={writeHistory} localPlayerId={localPlayerId}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}PerformMultiplayerAbandon: {ex.Message}");
        }
    }
}
