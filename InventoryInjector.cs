// Swaps the freshly-rolled starting inventory (character default
// deck/relics/potions) for the historical inventory reconstructed
// from RunHistory deltas. Called from inside the new-run pipeline
// BEFORE map gen, so relics that alter map generation
// (GoldenCompass → GoldenPathActMap, FurCoat → late hooks, etc.)
// are actually on the player when Hook.ModifyGeneratedMap fires.
//
// Splitting this out from RetryRunner lets the FinalizeStartingRelics
// Harmony patch share it without depending on the orchestration
// class. Mutations use silent=false on relics/potions so the HUD
// (NRelicInventory, NPotionContainer) re-renders.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

public static class InventoryInjector
{
    // silent=true suppresses HUD events (card-gain fly-in, relic-gain
    // flash, potion-gain animation). Useful for the act-map browser's
    // preview swaps where the user just wants the inventory state
    // updated quietly — calling Add/RemoveInternal(silent: silent) on
    // every entry would replay every acquisition animation, which is
    // noisy when previewing a different node or switching players.
    // When silent, callers are responsible for refreshing the HUD
    // widgets themselves (e.g. re-Initialize topBar.Hp/Gold/Deck).
    public static void Apply(RunState runState, PlayerStateSnapshot snapshot, bool silent = false)
    {
        var player = runState.Players.FirstOrDefault(pp => pp.NetId == snapshot.NetId)
                     ?? runState.Players[0];

        if (snapshot.ExactState != null)
        {
            try
            {
                player.SyncWithSerializedPlayer(snapshot.ExactState);
                GD.Print($"{RetryMod.LogPrefix}inject exact: netId={player.NetId} relics={player.Relics.Count} deck={player.Deck.Cards.Count} potions={player.Potions.Count()} slots={player.MaxPotionCount}");
                return;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}inject exact failed, falling back to reconstructed fields: {ex.Message}");
            }
        }

        // HP / Max HP / Gold — set via the internal setters so health
        // bar listeners pick up the change.
        try
        {
            player.Creature.SetMaxHpInternal(snapshot.MaxHp);
            player.Creature.SetCurrentHpInternal(snapshot.CurrentHp);
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}set hp: {ex.Message}"); }
        try { player.Gold = snapshot.Gold; }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}set gold: {ex.Message}"); }

        // ----- Relics: remove starting, add historical -----
        foreach (var relic in player.Relics.ToList())
        {
            try { player.RemoveRelicInternal(relic, silent: silent); }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}remove relic {relic?.Id}: {ex.Message}"); }
        }
        foreach (var sr in snapshot.Relics)
        {
            if (sr.Id == null) continue;
            // Drift detection: historical relic id might be gone from
            // the current ModelDb (mod removed, renamed, etc.). Skip
            // and surface a loud warning rather than crashing
            // FromSerializable later.
            if (ModelDb.GetByIdOrNull<RelicModel>(sr.Id) == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}DRIFT relic {sr.Id} not in ModelDb — skipping (mod changed since original run?)");
                continue;
            }
            try
            {
                var relic = RelicModel.FromSerializable(sr);
                player.AddRelicInternal(relic, -1, silent: silent);
            }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}add relic {sr.Id}: {ex.Message}"); }
        }

        // ----- Potions: discard starting, restore capacity, add historical -----
        // AddRelicInternal intentionally does not replay AfterObtained, so
        // pickup-only capacity effects (Potion Belt, Phial Holster,
        // Alchemical Coffer) must be restored explicitly.
        foreach (var potion in player.Potions.ToList())
        {
            try { player.DiscardPotionInternal(potion, silent: silent); }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}discard potion: {ex.Message}"); }
        }
        try
        {
            int slotDelta = snapshot.MaxPotionSlotCount - player.MaxPotionCount;
            if (slotDelta > 0) player.AddToMaxPotionCount(slotDelta);
            else if (slotDelta < 0) player.SubtractFromMaxPotionCount(-slotDelta);
        }
        catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}set potion slots: {ex.Message}"); }
        foreach (var sp in snapshot.Potions)
        {
            if (sp.Id == null) continue;
            try
            {
                var potion = PotionModel.FromSerializable(sp);
                player.AddPotionInternal(potion, sp.SlotIndex, silent: silent);
            }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}add potion {sp.Id}: {ex.Message}"); }
        }

        // ----- Deck: clear, repopulate from history -----
        // silent=false so the HUD's deck-count badge fires the
        // CardAdded/CardRemoved listeners. Without this, the badge
        // keeps showing the character's starting count (10 for
        // Ironclad) even after we've stuffed in 30 historical cards.
        foreach (var card in player.Deck.Cards.ToList())
        {
            try { player.Deck.RemoveInternal(card, silent: silent); }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}remove starting card {card?.Id}: {ex.Message}"); }
            try { runState.RemoveCard(card); }
            catch { }
        }
        foreach (var sc in snapshot.Deck)
        {
            if (sc.Id == null) continue;
            if (ModelDb.GetByIdOrNull<MegaCrit.Sts2.Core.Models.CardModel>(sc.Id) == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}DRIFT card {sc.Id} not in ModelDb — skipping");
                continue;
            }
            try
            {
                var card = runState.LoadCard(sc, player);
                player.Deck.AddInternal(card, -1, silent: silent);
            }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}add card {sc.Id}: {ex.Message}"); }
        }
        GD.Print($"{RetryMod.LogPrefix}inject summary: state={snapshot.Fidelity} relics={player.Relics.Count} deck={player.Deck.Cards.Count} potions={player.Potions.Count()} slots={player.MaxPotionCount}");
    }
}
