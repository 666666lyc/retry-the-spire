using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Runs;

namespace Retry;

internal static class SingleplayerHistoryLauncher
{
    internal static async Task LaunchAsync(RetryTarget target)
    {
        // RetryContext is read by Harmony patches that need to alter
        // standard new-run behavior:
        //   • IsRetrying gates everything (so unrelated new runs the
        //     player starts from the menu are untouched).
        //   • SkipNextNeowEntry suppresses the single auto-Neow
        //     EnterMapCoord inside EnterAct(0). We can't clear the
        //     StartedWithNeow flag instead, because GenerateMap reads
        //     it and rewrites StartingMapPoint.PointType from Ancient
        //     to Monster — which would shift the entire type-walk
        //     by one row and land us on the wrong node.
        RetryContext.IsRetrying = true;
        RetryContext.SkipNextNeowEntry = true;
        RetryContext.SourceStartTime = target.SourceStartTime;
        RetryContext.SourceSeed = target.Seed;
        try
        {
            // FromSerializable requires every act to have a non-null,
            // fully-populated SerializableRoomSet (boss id, events,
            // encounters) — which we can't reconstruct from history
            // because the finished-run RunHistory doesn't store room
            // composition. So we use the new-run pipeline instead:
            //   • StartNewSingleplayerRun creates a fresh RunState
            //     with the same seed and character, then generates
            //     rooms / map exactly like a normal new game.
            //   • InjectInventory swaps the freshly-rolled starting
            //     inventory for the reconstructed historical one,
            //     non-silently so the HUD picks it up.
            //   • If the target is on a later act, we hand off to
            //     RunManager.SetActInternal which advances and
            //     regenerates the map for that act.
            //   • Finally, we walk the new map matching the historical
            //     MapPointType per row to find the target coord, then
            //     EnterMapCoord drops the player into that room.
            // Mid-run retries are handled upstream in Begin(): the
            // abandon modal must run + complete (calling CleanUp +
            // DeleteCurrentRun) BEFORE we reach this point. If a run
            // is still in progress here, something skipped the modal.
            if (RunManager.Instance.IsInProgress)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}LaunchAsync: run still in progress — modal/cleanup skipped?");
                return;
            }

            if (NGame.Instance == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}LaunchAsync: NGame.Instance null");
                return;
            }

            var character = ModelDb.GetById<MegaCrit.Sts2.Core.Models.CharacterModel>(target.Player.CharacterId ?? ModelId.none);
            var acts = new List<MegaCrit.Sts2.Core.Models.ActModel>(target.ActIds.Count);
            foreach (var id in target.ActIds)
            {
                acts.Add(ModelDb.GetById<MegaCrit.Sts2.Core.Models.ActModel>(id));
            }

            var modifiers = new List<MegaCrit.Sts2.Core.Models.ModifierModel>(target.Modifiers.Count);
            foreach (var m in target.Modifiers)
            {
                try
                {
                    modifiers.Add(MegaCrit.Sts2.Core.Models.ModifierModel.FromSerializable(m));
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}skip modifier: {ex.Message}");
                }
            }

            try
            {
                await NGame.Instance.Transition.FadeOut(0.8f, character.CharacterSelectTransitionPath);
            }
            catch
            { /* aesthetic */
            }

            try
            {
                NAudioManager.Instance?.StopMusic();
            }
            catch
            {
            }

            // Preserve progression eligibility when replaying an unmodified
            // Standard history entry. The game only awards ascension
            // progression to Standard runs, so forcing every retry to Custom
            // prevented single-player history retries from unlocking the next
            // ascension level. Custom, daily, and modified histories remain
            // Custom so they cannot accidentally gain Standard progression.
            var retryGameMode = target.GameMode == GameMode.Standard && target.Modifiers.Count == 0 ? GameMode.Standard : GameMode.Custom;
            var runState = await NGame.Instance.StartNewSingleplayerRun(character, shouldSave: true, acts, modifiers, target.Seed, retryGameMode, target.Ascension);
            // EnterAct(0) inside StartRun has already fired by now;
            // any later EnterMapCoord(StartingMapPoint) is OURS (e.g.
            // when the target IS the StartingMapPoint of a later act
            // like act 1's Ancient). Clear the suppress flag so the
            // patch doesn't eat our explicit call.
            RetryContext.SkipNextNeowEntry = false;
            GD.Print($"{RetryMod.LogPrefix}post-StartNew: rngSeedString={runState.Rng.StringSeed} rngSeedUint={runState.Rng.Seed} currentAct={runState.CurrentActIndex} mapNull={runState.Map == null} startedNeow={runState.ExtraFields.StartedWithNeow}");
            DebugDump.DumpMap(runState);
            // Inject reconstructed inventory non-silently so the
            // relic / potion HUD picks up the changes. For act 0
            // this is fine post-launch because none of act 1's
            // relics participate in Hook.ModifyGeneratedMap. Later
            // acts where the player has GoldenCompass / FurCoat are
            // an edge case we'll handle separately.
            try
            {
                InventoryInjector.Apply(runState, target.Player);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}inject inventory: {ex.Message}\n{ex.StackTrace}");
            }

            // Offset the in-run timer so a retry doesn't read as
            // "0:00" — we want the speedrun-timer display to roughly
            // reflect where the original run was time-wise. Pro-rate
            // total RunTime by floor position; the historical death
            // node gets the full original time.
            try
            {
                RunTimeOffset.Apply(target);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}offset timer: {ex.Message}");
            }

            // RNG counter restoration happens later, inside
            // NavigateToTarget, once we've resolved the actual target
            // coord — the snapshot is keyed by coord and only that
            // call can find it.
            // Jump to target act if not already on it. SetActInternal
            // clears visited coords, resets ActFloor, and regenerates
            // the map for the new act.
            if (target.TargetActIndex > 0 && target.TargetActIndex < runState.Acts.Count)
            {
                try
                {
                    await RunManager.Instance.SetActInternal(target.TargetActIndex);
                    DebugDump.DumpMap(runState);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}SetActInternal: {ex.Message}");
                }
            }

            // Walk the new map matching the historical MapPointType
            // chain to locate the target coord, then enter that room.
            // Map generation is seeded so the new map matches the
            // historical layout exactly — the walk should always find
            // a unique path. If it doesn't (modded maps, edge cases),
            // FindTargetNode falls back to "any child with matching
            // type, else first child".
            try
            {
                await HistoryNavigation.NavigateToTarget(runState, target);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}navigate to target: {ex.Message}\n{ex.StackTrace}");
            }

            try
            {
                await NGame.Instance.Transition.FadeIn();
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}LaunchAsync: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            RetryContext.IsRetrying = false;
            RetryContext.SkipNextNeowEntry = false;
            RetryContext.SourceStartTime = 0;
            RetryContext.SourceSeed = null;
        }
    }
}
