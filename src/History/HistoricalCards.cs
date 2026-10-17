using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

internal static class HistoricalCards
{
    // SerializableCard equality includes Id + CurrentUpgradeLevel +
    // Enchantment — that's how the game distinguishes "remove the
    // upgraded Strike but keep the base Strike". Find the first
    // matching deck card.
    internal static int FindMatching(List<SerializableCard> deck, SerializableCard target)
    {
        return FindBestMatching(deck, target, excluded: null, preferUnenchanted: false);
    }

    internal static int FindBestMatching(List<SerializableCard> deck, SerializableCard target, HashSet<int>? excluded, bool preferUnenchanted)
    {
        int bestIndex = -1;
        int bestScore = int.MinValue;
        for (int i = 0; i < deck.Count; i++)
        {
            if (excluded?.Contains(i) == true)
                continue;
            var candidate = deck[i];
            if (!CardIdEquals(candidate.Id, target.Id))
                continue;
            int score = 0;
            if (!preferUnenchanted && candidate.Equals(target))
                score += 1000;
            if (candidate.CurrentUpgradeLevel == target.CurrentUpgradeLevel)
                score += 100;
            if (candidate.FloorAddedToDeck == target.FloorAddedToDeck)
                score += 20;
            if (EnchantmentEquals(candidate.Enchantment, target.Enchantment))
                score += 60;
            if (preferUnenchanted && candidate.Enchantment == null)
                score += 200;
            if (SavedPropertiesEqual(candidate.Props, target.Props))
                score += 30;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    internal static bool EnchantmentEquals(SerializableEnchantment? left, SerializableEnchantment? right) => left == null ? right == null : right != null && CardIdEquals(left.Id, right.Id) && left.Amount == right.Amount && SavedPropertiesEqual(left.Props, right.Props);
    internal static bool SavedPropertiesEqual(SavedProperties? left, SavedProperties? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left == null || right == null)
            return false;
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(left, JsonSerializationUtility.Options) == System.Text.Json.JsonSerializer.Serialize(right, JsonSerializationUtility.Options);
        }
        catch
        {
            return false;
        }
    }

    internal static SerializableCard? FindCardToUpgrade(List<SerializableCard> deck, ModelId id)
    {
        SerializableCard? result = null;
        foreach (var card in deck)
        {
            if (!CardIdEquals(card.Id, id))
                continue;
            if (result == null || card.CurrentUpgradeLevel < result.CurrentUpgradeLevel)
                result = card;
        }

        return result;
    }

    internal static SerializableCard? FindCardToDowngrade(List<SerializableCard> deck, ModelId id)
    {
        SerializableCard? result = null;
        foreach (var card in deck)
        {
            if (!CardIdEquals(card.Id, id))
                continue;
            if (result == null || card.CurrentUpgradeLevel > result.CurrentUpgradeLevel)
                result = card;
        }

        return result;
    }

    internal static bool CardIdEquals(ModelId? a, ModelId? b) => a != null && b != null && a.Equals(b);
    internal static SerializableCard CloneCard(SerializableCard src, int floorNum) => new()
    {
        Id = src.Id,
        CurrentUpgradeLevel = src.CurrentUpgradeLevel,
        Enchantment = CloneEnchantment(src.Enchantment),
        Props = src.Props,
        FloorAddedToDeck = floorNum,
    };
    internal static SerializableEnchantment? CloneEnchantment(SerializableEnchantment? src) => src == null ? null : new SerializableEnchantment
    {
        Id = src.Id,
        Amount = src.Amount,
        Props = src.Props,
    };
}
