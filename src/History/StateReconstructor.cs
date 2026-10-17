// Walks a RunHistory's per-floor entries up to the target node and
// folds the recorded deltas into a PlayerStateSnapshot. The result
// represents the player's state at the *start* of the target room
// (i.e. after the previous room's events resolved).
//
// Data sources per PlayerMapPointHistoryEntry:
//   • CardsGained / CardsRemoved      → deck mutations
//   • UpgradedCards / DowngradedCards → upgrade-level shifts
//   • CardsEnchanted                  → enchantment attached
//   • CardsTransformed                → in-place swap of card id
//   • RelicChoices (wasPicked)         → picked relic adds
//   • BoughtRelics                    → shop-bought relics
//   • RelicsRemoved                   → relic removals
//   • PotionChoices (wasPicked)        → picked potion adds
//   • BoughtPotions                   → shop-bought potions
//   • PotionDiscarded / PotionUsed    → potion removals
//
// Final HP / MaxHp / Gold are read from the entry just before the
// target rather than computed from deltas — the recorded numbers
// are authoritative and avoid drift from rounding edge cases.
using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

public static class StateReconstructor
{
    public static PlayerStateSnapshot ReconstructAtTarget(RunHistory history, int targetActIndex, int targetFloorIndex, RunHistoryPlayer player)
    {
        var exact = RngSnapshotStore.TryGetPlayerSnapshot(history.StartTime, history.Seed, targetActIndex, targetFloorIndex, player.Id);
        if (exact != null && exact.CharacterId != null && exact.CharacterId.Equals(player.Character))
        {
            var exactSnapshot = FromExactState(exact);
            GD.Print($"{RetryMod.LogPrefix}player state exact: netId={player.Id} act={targetActIndex} floor={targetFloorIndex} slots={exactSnapshot.MaxPotionSlotCount}");
            return exactSnapshot;
        }

        GD.Print($"{RetryMod.LogPrefix}player snapshot unavailable: netId={player.Id} act={targetActIndex} floor={targetFloorIndex}; falling back to history reconstruction");
        int basePotionSlots = Player.initialMaxPotionSlotCount;
        var ascension = new AscensionManager(history.Ascension);
        bool tightBelt = ascension.HasLevel(AscensionLevel.TightBelt);
        if (tightBelt)
            basePotionSlots--;
        var snapshot = new PlayerStateSnapshot
        {
            NetId = player.Id,
            CharacterId = player.Character,
            MaxPotionSlotCount = basePotionSlots,
        };
        SeedFromCharacter(snapshot, player.Character);
        int globalFloor = 0;
        bool silkenTressUsed = false;
        PlayerMapPointHistoryEntry? lastApplied = null;
        for (int act = 0; act < history.MapPointHistory.Count; act++)
        {
            var actEntries = history.MapPointHistory[act];
            int endFloor = (act == targetActIndex) ? targetFloorIndex : actEntries.Count;
            if (act > targetActIndex)
                break;
            for (int floor = 0; floor < endFloor; floor++)
            {
                globalFloor++;
                PlayerMapPointHistoryEntry entry;
                try
                {
                    entry = actEntries[floor].GetEntry(player.Id);
                }
                catch
                {
                    continue;
                }

                // Silken Tress enchants the first normal card reward after
                // it is obtained, then persists IsUsed=true as a saved relic
                // property. RunHistory records the offered cards but not a
                // per-floor snapshot of relic SavedProperties, so infer this
                // one-shot transition from the observable reward history.
                // Check ownership before ApplyDelta: a Tress obtained from
                // this room comes after this room's card reward and must not
                // consume itself retroactively.
                bool hadSilkenTress = HistoricalRelics.FindSilkenTress(snapshot.Relics) >= 0;
                bool consumedSilkenTress = hadSilkenTress && entry.CardChoices.Count > 0;
                ApplyDelta(snapshot, entry, globalFloor);
                bool hasSilkenTress = HistoricalRelics.FindSilkenTress(snapshot.Relics) >= 0;
                if (!hasSilkenTress || !hadSilkenTress)
                {
                    // Removed, or newly obtained/re-obtained on this floor.
                    silkenTressUsed = false;
                }
                else if (consumedSilkenTress)
                {
                    silkenTressUsed = true;
                }

                lastApplied = entry;
            }
        }

        HistoricalRelics.RestoreSilkenTressState(snapshot, player, silkenTressUsed);
        HistoricalRelics.BackfillStableRelicProperties(snapshot, player, history, targetActIndex, targetFloorIndex);
        // Override scalar fields from the last applied entry so HP /
        // gold are exactly what the recorded run reports rather than
        // a computed approximation.
        if (lastApplied != null)
        {
            snapshot.CurrentHp = lastApplied.CurrentHp;
            snapshot.MaxHp = lastApplied.MaxHp;
            snapshot.Gold = lastApplied.CurrentGold;
        }

        GD.Print($"{RetryMod.LogPrefix}player state reconstructed: netId={player.Id} ascension={history.Ascension} tightBelt={tightBelt} slots={snapshot.MaxPotionSlotCount} deck={snapshot.Deck.Count} relics={snapshot.Relics.Count}");
        HistoricalRelics.LogApproximateSavedProperties(snapshot);
        return snapshot;
    }

    internal static PlayerStateSnapshot FromExactState(SerializablePlayer exact) => new()
    {
        ExactState = exact,
        Fidelity = PlayerStateFidelity.Exact,
        NetId = exact.NetId,
        CharacterId = exact.CharacterId,
        CurrentHp = exact.CurrentHp,
        MaxHp = exact.MaxHp,
        Gold = exact.Gold,
        MaxPotionSlotCount = exact.MaxPotionSlotCount,
        Deck = new List<SerializableCard>(exact.Deck),
        Relics = new List<SerializableRelic>(exact.Relics),
        Potions = new List<SerializablePotion>(exact.Potions),
    };
    internal static void SeedFromCharacter(PlayerStateSnapshot s, ModelId characterId)
    {
        var character = ModelDb.GetByIdOrNull<CharacterModel>(characterId);
        if (character == null)
        {
            // Modded / removed character — leave the snapshot defaulted
            // and let the caller surface a helpful error.
            return;
        }

        s.CurrentHp = character.StartingHp;
        s.MaxHp = character.StartingHp;
        s.Gold = character.StartingGold;
        foreach (var card in character.StartingDeck)
        {
            s.Deck.Add(new SerializableCard { Id = card.Id, CurrentUpgradeLevel = 0, FloorAddedToDeck = 0, });
        }

        foreach (var relic in character.StartingRelics)
        {
            s.MaxPotionSlotCount += HistoricalPotions.PotionSlotBonus(relic.Id);
            s.Relics.Add(new SerializableRelic { Id = relic.Id, FloorAddedToDeck = 0, });
        }

        int slot = 0;
        foreach (var potion in character.StartingPotions)
        {
            s.Potions.Add(new SerializablePotion { Id = potion.Id, SlotIndex = slot++ });
        }
    }

    internal static void ApplyDelta(PlayerStateSnapshot s, PlayerMapPointHistoryEntry e, int floorNum)
    {
        // ----- Cards -----
        foreach (var c in e.CardsGained)
        {
            s.Deck.Add(HistoricalCards.CloneCard(c, floorNum));
        }

        foreach (var c in e.CardsRemoved)
        {
            int idx = HistoricalCards.FindMatching(s.Deck, c);
            if (idx >= 0)
                s.Deck.RemoveAt(idx);
        }

        foreach (var id in e.UpgradedCards)
        {
            // Run history only stores the card id for smith upgrades.  If the
            // deck contains multiple copies, repeatedly picking the first one
            // can push an already-upgraded copy past its maximum while leaving
            // the copy that was actually smithed untouched.  Besides producing
            // the wrong state, LoadCard rejects that impossible upgrade level
            // and the card disappears entirely.  Selecting the least-upgraded
            // matching copy preserves the only ordering information available
            // in history and never double-upgrades a maxed copy while another
            // eligible copy exists.
            var card = HistoricalCards.FindCardToUpgrade(s.Deck, id);
            if (card != null)
                card.CurrentUpgradeLevel++;
        }

        foreach (var id in e.DowngradedCards)
        {
            // Mirror upgrade selection: a downgrade must consume an upgraded
            // copy before considering a base copy with the same model id.
            var card = HistoricalCards.FindCardToDowngrade(s.Deck, id);
            if (card != null)
                card.CurrentUpgradeLevel = Math.Max(0, card.CurrentUpgradeLevel - 1);
        }

        // A room can enchant several identical cards (Goopy commonly enchants
        // every basic Defend). Each history record must consume a distinct
        // deck instance; repeatedly selecting the first id match collapses the
        // whole batch onto one card.
        var enchantedThisBatch = new HashSet<int>();
        foreach (var ench in e.CardsEnchanted)
        {
            if (ench.Card.Id == null)
                continue;
            int index = HistoricalCards.FindBestMatching(s.Deck, ench.Card, enchantedThisBatch, preferUnenchanted: true);
            if (index < 0)
                continue;
            enchantedThisBatch.Add(index);
            var card = s.Deck[index];
            // Prefer the embedded post-enchant SerializableEnchantment
            // from ench.Card.Enchantment — it carries Amount (and any
            // other future fields) which the bare `ench.Enchantment`
            // id doesn't. Constructing a fresh SerializableEnchantment
            // with only Id loses the block/damage value, which is why
            // retried enchanted cards showed "Gain 0 Block" etc.
            card.Enchantment = HistoricalCards.CloneEnchantment(ench.Card.Enchantment) ?? new SerializableEnchantment
            {
                Id = ench.Enchantment
            };
            GD.Print($"{RetryMod.LogPrefix}enchant instance match: " + $"card={card.Id} index={index} upgrade={card.CurrentUpgradeLevel} " + $"floorAdded={card.FloorAddedToDeck} enchant={card.Enchantment.Id} " + $"amount={card.Enchantment.Amount}");
        }

        foreach (var trans in e.CardsTransformed)
        {
            int idx = HistoricalCards.FindMatching(s.Deck, trans.OriginalCard);
            if (idx >= 0)
            {
                s.Deck[idx] = HistoricalCards.CloneCard(trans.FinalCard, floorNum);
            }
        }

        // ----- Relics -----
        // RelicChoices already covers shop purchases (every bought
        // relic shows up there with wasPicked=true) — iterating
        // BoughtRelics on top of that produced two copies of every
        // shop-bought relic. Verified across 20 SP runs: bought_relics
        // is always a strict subset of relic_choices[wasPicked].
        foreach (var pick in e.RelicChoices)
        {
            if (!pick.wasPicked)
                continue;
            HistoricalPotions.AdjustPotionSlotCount(s, HistoricalPotions.PotionSlotBonus(pick.choice), $"gain {pick.choice}");
            s.Relics.Add(new SerializableRelic { Id = pick.choice, FloorAddedToDeck = floorNum });
        }

        foreach (var id in e.RelicsRemoved)
        {
            int idx = s.Relics.FindIndex(r => r.Id != null && r.Id.Equals(id));
            if (idx >= 0)
            {
                HistoricalPotions.AdjustPotionSlotCount(s, -HistoricalPotions.PotionSlotBonus(s.Relics[idx].Id), $"remove {s.Relics[idx].Id}");
                s.Relics.RemoveAt(idx);
            }
        }

        foreach (string choice in e.RestSiteChoices)
        {
            if (string.Equals(choice, "LIFT", StringComparison.OrdinalIgnoreCase))
                HistoricalRelics.IncrementGirya(s.Relics);
        }

        // ----- Potions -----
        // PotionChoices can contain potions generated by a potion that was
        // itself used in this room (for example Entropic Brew).  Applying all
        // gains before all uses leaves the consumed potion occupying a slot,
        // so later generated potions are incorrectly dropped as "inventory
        // full".  First consume removals that match the inventory entering the
        // room.  A removal that is not present yet belongs to a potion gained
        // and consumed in this same room; keep it pending and consume that gain
        // at its recorded position.  This preserves the slots available to all
        // subsequent gains.
        var pendingPotionRemovals = new List<ModelId>();
        foreach (var id in e.PotionUsed)
        {
            if (!HistoricalPotions.RemovePotion(s, id))
                pendingPotionRemovals.Add(id);
        }

        foreach (var id in e.PotionDiscarded)
        {
            if (!HistoricalPotions.RemovePotion(s, id))
                pendingPotionRemovals.Add(id);
        }

        // Same shape as relics — bought_potions is redundant with
        // potion_choices[wasPicked].
        foreach (var pick in e.PotionChoices)
        {
            if (!pick.wasPicked)
                continue;
            int pendingIndex = pendingPotionRemovals.FindIndex(id => id.Equals(pick.choice));
            if (pendingIndex >= 0)
            {
                // Acquired and consumed/discarded before leaving this room.
                pendingPotionRemovals.RemoveAt(pendingIndex);
                continue;
            }

            HistoricalPotions.AddPotion(s, pick.choice);
        }

        // Defensive fallback for unusual history ordering: if a pending
        // removal did not correspond to a recorded picked potion, try it once
        // more against the resulting inventory.
        foreach (var id in pendingPotionRemovals)
            HistoricalPotions.RemovePotion(s, id);
    // ----- Bought colorless cards (shop) -----
    // BoughtColorless is redundant with CardsGained for the same
    // shop floor — the CardsGained pass at the top already added
    // the purchase, so we deliberately don't iterate it here.
    }
}
