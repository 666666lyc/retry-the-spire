using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

internal static class HistoricalRelics
{
    internal static void IncrementGirya(List<SerializableRelic> relics)
    {
        for (int i = 0; i < relics.Count; i++)
        {
            var saved = relics[i];
            if (saved.Id == null || ModelDb.GetByIdOrNull<RelicModel>(saved.Id)is not Girya)
                continue;
            try
            {
                if (RelicModel.FromSerializable(saved)is not Girya girya)
                    return;
                girya.TimesLifted = Math.Min(Girya.maxLifts, girya.TimesLifted + 1);
                var updated = girya.ToSerializable();
                updated.FloorAddedToDeck = saved.FloorAddedToDeck;
                relics[i] = updated;
                GD.Print($"{RetryMod.LogPrefix}Girya instance lift: " + $"index={i} floorAdded={saved.FloorAddedToDeck} times={girya.TimesLifted}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}restore Girya lift: {ex.Message}");
            }

            return;
        }
    }

    internal static int FindSilkenTress(List<SerializableRelic> relics)
    {
        for (int i = 0; i < relics.Count; i++)
        {
            var id = relics[i].Id;
            if (id != null && ModelDb.GetByIdOrNull<RelicModel>(id) is SilkenTress)
                return i;
        }

        return -1;
    }

    internal static void RestoreSilkenTressState(PlayerStateSnapshot snapshot, RunHistoryPlayer historyPlayer, bool isUsed)
    {
        if (!isUsed)
            return;
        int index = FindSilkenTress(snapshot.Relics);
        if (index < 0)
            return;
        var current = snapshot.Relics[index];
        // Prefer the game's own final serialized property bag when it is
        // available. This avoids duplicating SavedProperties' private data
        // representation and remains forward-compatible with extra fields.
        var finalState = historyPlayer.Relics.FirstOrDefault(r => r.Id != null && ModelDb.GetByIdOrNull<RelicModel>(r.Id) is SilkenTress);
        if (finalState?.Props != null)
        {
            current.Props = finalState.Props;
            return;
        }

        // A relic may have been removed later and therefore be absent from
        // RunHistoryPlayer.Relics. In that case, ask the original model to
        // serialize its used state instead of constructing SavedProperties
        // by hand.
        if (current.Id == null)
            return;
        if (ModelDb.GetByIdOrNull<RelicModel>(current.Id)?.ToMutable()is not SilkenTress relic)
            return;
        var isUsedProperty = typeof(SilkenTress).GetProperty("IsUsed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (isUsedProperty?.SetMethod == null)
            return;
        isUsedProperty.SetValue(relic, true);
        var serialized = relic.ToSerializable();
        serialized.FloorAddedToDeck = current.FloorAddedToDeck;
        snapshot.Relics[index] = serialized;
    }

    internal static void BackfillStableRelicProperties(PlayerStateSnapshot snapshot, RunHistoryPlayer historyPlayer, RunHistory history, int targetActIndex, int targetFloorIndex)
    {
        foreach (var current in snapshot.Relics)
        {
            if (current.Id == null)
                continue;
            if (snapshot.Relics.Count(r => HistoricalCards.CardIdEquals(r.Id, current.Id)) != 1)
                continue;
            var finalMatches = historyPlayer.Relics.Where(r => HistoricalCards.CardIdEquals(r.Id, current.Id)).ToList();
            if (finalMatches.Count != 1 || finalMatches[0].Props == null)
                continue;
            if (HasFutureRelicRemoval(history, historyPlayer.Id, current.Id, targetActIndex, targetFloorIndex))
                continue;
            var model = ModelDb.GetByIdOrNull<RelicModel>(current.Id);
            if (!HasAcquisitionFixedSavedProperties(model))
                continue;
            current.Props = finalMatches[0].Props;
            GD.Print($"{RetryMod.LogPrefix}relic props backfilled: {current.Id} (unique acquisition-fixed instance)");
        }
    }

    internal static bool HasFutureRelicRemoval(RunHistory history, ulong playerId, ModelId relicId, int targetActIndex, int targetFloorIndex)
    {
        for (int act = targetActIndex; act < history.MapPointHistory.Count; act++)
        {
            int startFloor = act == targetActIndex ? targetFloorIndex : 0;
            for (int floor = startFloor; floor < history.MapPointHistory[act].Count; floor++)
            {
                try
                {
                    var entry = history.MapPointHistory[act][floor].GetEntry(playerId);
                    if (entry.RelicsRemoved.Any(id => HistoricalCards.CardIdEquals(id, relicId)))
                        return true;
                }
                catch
                {
                }
            }
        }

        return false;
    }

    internal static bool HasAcquisitionFixedSavedProperties(RelicModel? model) => model is ArchaicTooth or Byrdpip or DustyTome or GoldenCompass or PaelsLegion or SeaGlass or TouchOfOrobas;
    internal static void LogApproximateSavedProperties(PlayerStateSnapshot snapshot)
    {
        var approximate = new HashSet<string>();
        foreach (var saved in snapshot.Relics)
        {
            if (saved.Id == null)
                continue;
            var model = ModelDb.GetByIdOrNull<RelicModel>(saved.Id);
            if (model is Girya or SilkenTress)
                continue;
            if (HasAcquisitionFixedSavedProperties(model) && saved.Props != null)
                continue;
            AddSavedPropertyDescription(approximate, model);
        }

        foreach (var saved in snapshot.Deck)
        {
            if (saved.Id != null)
                AddSavedPropertyDescription(approximate, ModelDb.GetByIdOrNull<CardModel>(saved.Id));
            if (saved.Enchantment?.Id != null)
                AddSavedPropertyDescription(approximate, ModelDb.GetByIdOrNull<EnchantmentModel>(saved.Enchantment.Id));
        }

        if (approximate.Count > 0)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}approximate saved properties (legacy history has no room snapshot): {string.Join(", ", approximate.OrderBy(x => x))}");
        }
    }

    internal static void AddSavedPropertyDescription(HashSet<string> destination, AbstractModel? model)
    {
        if (model == null)
            return;
        var members = model.GetType().GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(member => member.GetCustomAttributes(typeof(SavedPropertyAttribute), inherit: true).Length > 0).Select(member => member.Name).Distinct().OrderBy(name => name).ToList();
        if (members.Count > 0)
            destination.Add($"{model.Id}[{string.Join("/", members)}]");
    }
}
