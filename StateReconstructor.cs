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
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

public static class StateReconstructor
{
    public static PlayerStateSnapshot ReconstructAtTarget(
        RunHistory history,
        int targetActIndex,
        int targetFloorIndex,
        RunHistoryPlayer player)
    {
        var exact = RngSnapshotStore.TryGetPlayerSnapshot(
            history.StartTime, history.Seed, targetActIndex, targetFloorIndex, player.Id);
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
        if (tightBelt) basePotionSlots--;
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
            if (act > targetActIndex) break;
            for (int floor = 0; floor < endFloor; floor++)
            {
                globalFloor++;
                PlayerMapPointHistoryEntry entry;
                try { entry = actEntries[floor].GetEntry(player.Id); }
                catch { continue; }

                // Silken Tress enchants the first normal card reward after
                // it is obtained, then persists IsUsed=true as a saved relic
                // property. RunHistory records the offered cards but not a
                // per-floor snapshot of relic SavedProperties, so infer this
                // one-shot transition from the observable reward history.
                // Check ownership before ApplyDelta: a Tress obtained from
                // this room comes after this room's card reward and must not
                // consume itself retroactively.
                bool hadSilkenTress = FindSilkenTress(snapshot.Relics) >= 0;
                bool consumedSilkenTress = hadSilkenTress && entry.CardChoices.Count > 0;

                ApplyDelta(snapshot, entry, globalFloor);

                bool hasSilkenTress = FindSilkenTress(snapshot.Relics) >= 0;
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

        RestoreSilkenTressState(snapshot, player, silkenTressUsed);
        BackfillStableRelicProperties(
            snapshot, player, history, targetActIndex, targetFloorIndex);

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
        LogApproximateSavedProperties(snapshot);

        return snapshot;
    }

    private static PlayerStateSnapshot FromExactState(SerializablePlayer exact) => new()
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

    private static void SeedFromCharacter(PlayerStateSnapshot s, ModelId characterId)
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
            s.Deck.Add(new SerializableCard
            {
                Id = card.Id,
                CurrentUpgradeLevel = 0,
                FloorAddedToDeck = 0,
            });
        }
        foreach (var relic in character.StartingRelics)
        {
            s.MaxPotionSlotCount += PotionSlotBonus(relic.Id);
            s.Relics.Add(new SerializableRelic
            {
                Id = relic.Id,
                FloorAddedToDeck = 0,
            });
        }
        int slot = 0;
        foreach (var potion in character.StartingPotions)
        {
            s.Potions.Add(new SerializablePotion { Id = potion.Id, SlotIndex = slot++ });
        }
    }

    private static void ApplyDelta(PlayerStateSnapshot s, PlayerMapPointHistoryEntry e, int floorNum)
    {
        // ----- Cards -----
        foreach (var c in e.CardsGained)
        {
            s.Deck.Add(CloneCard(c, floorNum));
        }
        foreach (var c in e.CardsRemoved)
        {
            int idx = FindMatching(s.Deck, c);
            if (idx >= 0) s.Deck.RemoveAt(idx);
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
            var card = FindCardToUpgrade(s.Deck, id);
            if (card != null) card.CurrentUpgradeLevel++;
        }
        foreach (var id in e.DowngradedCards)
        {
            // Mirror upgrade selection: a downgrade must consume an upgraded
            // copy before considering a base copy with the same model id.
            var card = FindCardToDowngrade(s.Deck, id);
            if (card != null) card.CurrentUpgradeLevel = Math.Max(0, card.CurrentUpgradeLevel - 1);
        }
        // A room can enchant several identical cards (Goopy commonly enchants
        // every basic Defend). Each history record must consume a distinct
        // deck instance; repeatedly selecting the first id match collapses the
        // whole batch onto one card.
        var enchantedThisBatch = new HashSet<int>();
        foreach (var ench in e.CardsEnchanted)
        {
            if (ench.Card.Id == null) continue;
            int index = FindBestMatching(s.Deck, ench.Card, enchantedThisBatch, preferUnenchanted: true);
            if (index < 0) continue;
            enchantedThisBatch.Add(index);
            var card = s.Deck[index];
            // Prefer the embedded post-enchant SerializableEnchantment
            // from ench.Card.Enchantment — it carries Amount (and any
            // other future fields) which the bare `ench.Enchantment`
            // id doesn't. Constructing a fresh SerializableEnchantment
            // with only Id loses the block/damage value, which is why
            // retried enchanted cards showed "Gain 0 Block" etc.
            card.Enchantment = CloneEnchantment(ench.Card.Enchantment)
                ?? new SerializableEnchantment { Id = ench.Enchantment };
            GD.Print(
                $"{RetryMod.LogPrefix}enchant instance match: " +
                $"card={card.Id} index={index} upgrade={card.CurrentUpgradeLevel} " +
                $"floorAdded={card.FloorAddedToDeck} enchant={card.Enchantment.Id} " +
                $"amount={card.Enchantment.Amount}");
        }
        foreach (var trans in e.CardsTransformed)
        {
            int idx = FindMatching(s.Deck, trans.OriginalCard);
            if (idx >= 0)
            {
                s.Deck[idx] = CloneCard(trans.FinalCard, floorNum);
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
            if (!pick.wasPicked) continue;
            AdjustPotionSlotCount(s, PotionSlotBonus(pick.choice), $"gain {pick.choice}");
            s.Relics.Add(new SerializableRelic { Id = pick.choice, FloorAddedToDeck = floorNum });
        }
        foreach (var id in e.RelicsRemoved)
        {
            int idx = s.Relics.FindIndex(r => r.Id != null && r.Id.Equals(id));
            if (idx >= 0)
            {
                AdjustPotionSlotCount(
                    s,
                    -PotionSlotBonus(s.Relics[idx].Id),
                    $"remove {s.Relics[idx].Id}");
                s.Relics.RemoveAt(idx);
            }
        }

        foreach (string choice in e.RestSiteChoices)
        {
            if (string.Equals(choice, "LIFT", StringComparison.OrdinalIgnoreCase))
                IncrementGirya(s.Relics);
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
            if (!RemovePotion(s, id)) pendingPotionRemovals.Add(id);
        }
        foreach (var id in e.PotionDiscarded)
        {
            if (!RemovePotion(s, id)) pendingPotionRemovals.Add(id);
        }

        // Same shape as relics — bought_potions is redundant with
        // potion_choices[wasPicked].
        foreach (var pick in e.PotionChoices)
        {
            if (!pick.wasPicked) continue;

            int pendingIndex = pendingPotionRemovals.FindIndex(id => id.Equals(pick.choice));
            if (pendingIndex >= 0)
            {
                // Acquired and consumed/discarded before leaving this room.
                pendingPotionRemovals.RemoveAt(pendingIndex);
                continue;
            }

            AddPotion(s, pick.choice);
        }

        // Defensive fallback for unusual history ordering: if a pending
        // removal did not correspond to a recorded picked potion, try it once
        // more against the resulting inventory.
        foreach (var id in pendingPotionRemovals) RemovePotion(s, id);

        // ----- Bought colorless cards (shop) -----
        // BoughtColorless is redundant with CardsGained for the same
        // shop floor — the CardsGained pass at the top already added
        // the purchase, so we deliberately don't iterate it here.
    }

    // SerializableCard equality includes Id + CurrentUpgradeLevel +
    // Enchantment — that's how the game distinguishes "remove the
    // upgraded Strike but keep the base Strike". Find the first
    // matching deck card.
    private static int FindMatching(List<SerializableCard> deck, SerializableCard target)
    {
        return FindBestMatching(deck, target, excluded: null, preferUnenchanted: false);
    }

    private static int FindBestMatching(
        List<SerializableCard> deck,
        SerializableCard target,
        HashSet<int>? excluded,
        bool preferUnenchanted)
    {
        int bestIndex = -1;
        int bestScore = int.MinValue;
        for (int i = 0; i < deck.Count; i++)
        {
            if (excluded?.Contains(i) == true) continue;
            var candidate = deck[i];
            if (!CardIdEquals(candidate.Id, target.Id)) continue;

            int score = 0;
            if (!preferUnenchanted && candidate.Equals(target)) score += 1000;
            if (candidate.CurrentUpgradeLevel == target.CurrentUpgradeLevel) score += 100;
            if (candidate.FloorAddedToDeck == target.FloorAddedToDeck) score += 20;
            if (EnchantmentEquals(candidate.Enchantment, target.Enchantment)) score += 60;
            if (preferUnenchanted && candidate.Enchantment == null) score += 200;
            if (SavedPropertiesEqual(candidate.Props, target.Props)) score += 30;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private static bool EnchantmentEquals(
        SerializableEnchantment? left,
        SerializableEnchantment? right)
        => left == null
            ? right == null
            : right != null
                && CardIdEquals(left.Id, right.Id)
                && left.Amount == right.Amount
                && SavedPropertiesEqual(left.Props, right.Props);

    private static bool SavedPropertiesEqual(SavedProperties? left, SavedProperties? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null) return false;
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(left, JsonSerializationUtility.Options)
                == System.Text.Json.JsonSerializer.Serialize(right, JsonSerializationUtility.Options);
        }
        catch { return false; }
    }

    private static SerializableCard? FindCardToUpgrade(List<SerializableCard> deck, ModelId id)
    {
        SerializableCard? result = null;
        foreach (var card in deck)
        {
            if (!CardIdEquals(card.Id, id)) continue;
            if (result == null || card.CurrentUpgradeLevel < result.CurrentUpgradeLevel)
                result = card;
        }
        return result;
    }

    private static SerializableCard? FindCardToDowngrade(List<SerializableCard> deck, ModelId id)
    {
        SerializableCard? result = null;
        foreach (var card in deck)
        {
            if (!CardIdEquals(card.Id, id)) continue;
            if (result == null || card.CurrentUpgradeLevel > result.CurrentUpgradeLevel)
                result = card;
        }
        return result;
    }

    private static bool CardIdEquals(ModelId? a, ModelId? b) =>
        a != null && b != null && a.Equals(b);

    private static int PotionSlotBonus(ModelId? id)
    {
        if (id == null) return 0;
        return ModelDb.GetByIdOrNull<RelicModel>(id) switch
        {
            PotionBelt => 2,
            PhialHolster => 1,
            AlchemicalCoffer => 3,
            _ => 0,
        };
    }

    private static void AdjustPotionSlotCount(
        PlayerStateSnapshot snapshot,
        int delta,
        string reason,
        bool writeLog = true)
    {
        if (delta == 0) return;
        int oldCount = snapshot.MaxPotionSlotCount;
        int newCount = Math.Max(0, oldCount + delta);

        // Player.SetMaxPotionCountInternal removes slots from the right. A
        // potion in a removed slot is first moved into the lowest empty slot
        // that survives the shrink, and is discarded only when none exists.
        // Reproduce that native ordering for histories that predate snapshots.
        if (newCount < oldCount)
        {
            for (int removedSlot = oldCount - 1; removedSlot >= newCount; removedSlot--)
            {
                var potion = snapshot.Potions.FirstOrDefault(p => p.SlotIndex == removedSlot);
                if (potion == null) continue;

                var occupied = new HashSet<int>(snapshot.Potions.Select(p => p.SlotIndex));
                int destination = -1;
                for (int slot = 0; slot < newCount; slot++)
                {
                    if (occupied.Contains(slot)) continue;
                    destination = slot;
                    break;
                }

                if (destination >= 0) potion.SlotIndex = destination;
                else snapshot.Potions.Remove(potion);
            }
        }

        snapshot.MaxPotionSlotCount = newCount;
        if (writeLog)
            GD.Print($"{RetryMod.LogPrefix}potion slots: {reason} {oldCount}->{newCount}");
    }

    private static void IncrementGirya(List<SerializableRelic> relics)
    {
        for (int i = 0; i < relics.Count; i++)
        {
            var saved = relics[i];
            if (saved.Id == null || ModelDb.GetByIdOrNull<RelicModel>(saved.Id) is not Girya)
                continue;
            try
            {
                if (RelicModel.FromSerializable(saved) is not Girya girya) return;
                girya.TimesLifted = Math.Min(Girya.maxLifts, girya.TimesLifted + 1);
                var updated = girya.ToSerializable();
                updated.FloorAddedToDeck = saved.FloorAddedToDeck;
                relics[i] = updated;
                GD.Print(
                    $"{RetryMod.LogPrefix}Girya instance lift: " +
                    $"index={i} floorAdded={saved.FloorAddedToDeck} times={girya.TimesLifted}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}restore Girya lift: {ex.Message}");
            }
            return;
        }
    }

    private static int FindSilkenTress(List<SerializableRelic> relics)
    {
        for (int i = 0; i < relics.Count; i++)
        {
            var id = relics[i].Id;
            if (id != null && ModelDb.GetByIdOrNull<RelicModel>(id) is SilkenTress)
                return i;
        }
        return -1;
    }

    private static void RestoreSilkenTressState(
        PlayerStateSnapshot snapshot,
        RunHistoryPlayer historyPlayer,
        bool isUsed)
    {
        if (!isUsed) return;

        int index = FindSilkenTress(snapshot.Relics);
        if (index < 0) return;

        var current = snapshot.Relics[index];

        // Prefer the game's own final serialized property bag when it is
        // available. This avoids duplicating SavedProperties' private data
        // representation and remains forward-compatible with extra fields.
        var finalState = historyPlayer.Relics.FirstOrDefault(r =>
            r.Id != null && ModelDb.GetByIdOrNull<RelicModel>(r.Id) is SilkenTress);
        if (finalState?.Props != null)
        {
            current.Props = finalState.Props;
            return;
        }

        // A relic may have been removed later and therefore be absent from
        // RunHistoryPlayer.Relics. In that case, ask the original model to
        // serialize its used state instead of constructing SavedProperties
        // by hand.
        if (current.Id == null) return;
        if (ModelDb.GetByIdOrNull<RelicModel>(current.Id)?.ToMutable() is not SilkenTress relic)
            return;
        var isUsedProperty = typeof(SilkenTress).GetProperty(
            "IsUsed",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        if (isUsedProperty?.SetMethod == null) return;
        isUsedProperty.SetValue(relic, true);
        var serialized = relic.ToSerializable();
        serialized.FloorAddedToDeck = current.FloorAddedToDeck;
        snapshot.Relics[index] = serialized;
    }

    private static void BackfillStableRelicProperties(
        PlayerStateSnapshot snapshot,
        RunHistoryPlayer historyPlayer,
        RunHistory history,
        int targetActIndex,
        int targetFloorIndex)
    {
        foreach (var current in snapshot.Relics)
        {
            if (current.Id == null) continue;
            if (snapshot.Relics.Count(r => CardIdEquals(r.Id, current.Id)) != 1) continue;
            var finalMatches = historyPlayer.Relics
                .Where(r => CardIdEquals(r.Id, current.Id))
                .ToList();
            if (finalMatches.Count != 1 || finalMatches[0].Props == null) continue;
            if (HasFutureRelicRemoval(
                    history, historyPlayer.Id, current.Id,
                    targetActIndex, targetFloorIndex))
                continue;

            var model = ModelDb.GetByIdOrNull<RelicModel>(current.Id);
            if (!HasAcquisitionFixedSavedProperties(model)) continue;
            current.Props = finalMatches[0].Props;
            GD.Print($"{RetryMod.LogPrefix}relic props backfilled: {current.Id} (unique acquisition-fixed instance)");
        }
    }

    private static bool HasFutureRelicRemoval(
        RunHistory history,
        ulong playerId,
        ModelId relicId,
        int targetActIndex,
        int targetFloorIndex)
    {
        for (int act = targetActIndex; act < history.MapPointHistory.Count; act++)
        {
            int startFloor = act == targetActIndex ? targetFloorIndex : 0;
            for (int floor = startFloor; floor < history.MapPointHistory[act].Count; floor++)
            {
                try
                {
                    var entry = history.MapPointHistory[act][floor].GetEntry(playerId);
                    if (entry.RelicsRemoved.Any(id => CardIdEquals(id, relicId))) return true;
                }
                catch { }
            }
        }
        return false;
    }

    private static bool HasAcquisitionFixedSavedProperties(RelicModel? model) => model is
        ArchaicTooth or Byrdpip or DustyTome or GoldenCompass or
        PaelsLegion or SeaGlass or TouchOfOrobas;

    private static void LogApproximateSavedProperties(PlayerStateSnapshot snapshot)
    {
        var approximate = new HashSet<string>();
        foreach (var saved in snapshot.Relics)
        {
            if (saved.Id == null) continue;
            var model = ModelDb.GetByIdOrNull<RelicModel>(saved.Id);
            if (model is Girya or SilkenTress) continue;
            if (HasAcquisitionFixedSavedProperties(model) && saved.Props != null) continue;
            AddSavedPropertyDescription(approximate, model);
        }
        foreach (var saved in snapshot.Deck)
        {
            if (saved.Id != null)
                AddSavedPropertyDescription(
                    approximate, ModelDb.GetByIdOrNull<CardModel>(saved.Id));
            if (saved.Enchantment?.Id != null)
                AddSavedPropertyDescription(
                    approximate,
                    ModelDb.GetByIdOrNull<EnchantmentModel>(saved.Enchantment.Id));
        }

        if (approximate.Count > 0)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}approximate saved properties (legacy history has no room snapshot): {string.Join(", ", approximate.OrderBy(x => x))}");
        }
    }

    private static void AddSavedPropertyDescription(
        HashSet<string> destination,
        AbstractModel? model)
    {
        if (model == null) return;
        var members = model.GetType()
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(member => member.GetCustomAttributes(
                typeof(SavedPropertyAttribute), inherit: true).Length > 0)
            .Select(member => member.Name)
            .Distinct()
            .OrderBy(name => name)
            .ToList();
        if (members.Count > 0)
            destination.Add($"{model.Id}[{string.Join("/", members)}]");
    }

    private static SerializableCard CloneCard(SerializableCard src, int floorNum) => new()
    {
        Id = src.Id,
        CurrentUpgradeLevel = src.CurrentUpgradeLevel,
        Enchantment = CloneEnchantment(src.Enchantment),
        Props = src.Props,
        FloorAddedToDeck = floorNum,
    };

    private static SerializableEnchantment? CloneEnchantment(SerializableEnchantment? src) =>
        src == null
            ? null
            : new SerializableEnchantment
            {
                Id = src.Id,
                Amount = src.Amount,
                Props = src.Props,
            };

    private static void AddPotion(PlayerStateSnapshot s, ModelId id)
    {
        // Find first free slot 0..MaxPotionSlotCount-1.
        var occupied = new HashSet<int>(s.Potions.Select(p => p.SlotIndex));
        for (int slot = 0; slot < s.MaxPotionSlotCount; slot++)
        {
            if (occupied.Contains(slot)) continue;
            s.Potions.Add(new SerializablePotion { Id = id, SlotIndex = slot });
            return;
        }
        // All slots full — drop. The game would have refused this in
        // the original run, so historical state shouldn't reach here.
    }

    private static bool RemovePotion(PlayerStateSnapshot s, ModelId id)
    {
        int idx = s.Potions.FindIndex(p => p.Id != null && p.Id.Equals(id));
        if (idx < 0) return false;
        s.Potions.RemoveAt(idx);
        return true;
    }
}
