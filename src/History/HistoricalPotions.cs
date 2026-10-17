using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

internal static class HistoricalPotions
{
    internal static int PotionSlotBonus(ModelId? id)
    {
        if (id == null)
            return 0;
        return ModelDb.GetByIdOrNull<RelicModel>(id) switch
        {
            PotionBelt => 2,
            PhialHolster => 1,
            AlchemicalCoffer => 3,
            _ => 0,
        };
    }

    internal static void AdjustPotionSlotCount(PlayerStateSnapshot snapshot, int delta, string reason, bool writeLog = true)
    {
        if (delta == 0)
            return;
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
                if (potion == null)
                    continue;
                var occupied = new HashSet<int>(snapshot.Potions.Select(p => p.SlotIndex));
                int destination = -1;
                for (int slot = 0; slot < newCount; slot++)
                {
                    if (occupied.Contains(slot))
                        continue;
                    destination = slot;
                    break;
                }

                if (destination >= 0)
                    potion.SlotIndex = destination;
                else
                    snapshot.Potions.Remove(potion);
            }
        }

        snapshot.MaxPotionSlotCount = newCount;
        if (writeLog)
            GD.Print($"{RetryMod.LogPrefix}potion slots: {reason} {oldCount}->{newCount}");
    }

    internal static void AddPotion(PlayerStateSnapshot s, ModelId id)
    {
        // Find first free slot 0..MaxPotionSlotCount-1.
        var occupied = new HashSet<int>(s.Potions.Select(p => p.SlotIndex));
        for (int slot = 0; slot < s.MaxPotionSlotCount; slot++)
        {
            if (occupied.Contains(slot))
                continue;
            s.Potions.Add(new SerializablePotion { Id = id, SlotIndex = slot });
            return;
        }
    // All slots full — drop. The game would have refused this in
    // the original run, so historical state shouldn't reach here.
    }

    internal static bool RemovePotion(PlayerStateSnapshot s, ModelId id)
    {
        int idx = s.Potions.FindIndex(p => p.Id != null && p.Id.Equals(id));
        if (idx < 0)
            return false;
        s.Potions.RemoveAt(idx);
        return true;
    }
}
