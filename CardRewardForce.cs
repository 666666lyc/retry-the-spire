// Force the target room's card reward to match the historical
// 3-card offer the original player saw. Without this, even if the
// retry reaches the right combat with the right encounter, the
// post-combat card reward is rolled fresh from CombatCardGeneration
// RNG → almost certainly different cards from the original.
//
// History (`PlayerMapPointHistoryEntry.CardChoices`) records all
// offered cards plus which one was picked. We extract the offered
// IDs from the target's entry, stash them in RetryContext, and
// intercept the next `CardFactory.CreateForReward` call to return
// our substitute list. The Prefix is one-shot — clears the context
// after firing so subsequent rewards (if any) use vanilla RNG.
//
// The full SerializableCard must be loaded, rather than recreating
// the card from its ModelId and upgrade count. Reward cards can carry
// persistent state such as Silken Tress' Glam enchantment and
// card-specific SavedProperties. Recreating only the canonical model
// made those historical properties disappear from the retried reward.
//
// Limitations:
//   • Only forces the target room's reward, not rewards from prior
//     floors (those didn't happen — we skipped them).
//   • If a card in history is no longer in ModelDb (modded run), we
//     warn and skip; the reward falls back to whatever RNG produces.
//   • The forced cards already contain the historical result of any
//     relic-driven mutation. The original reward pipeline is skipped
//     so one-shot effects such as Silken Tress are not applied twice.
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace Retry;

[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.CreateForReward),
    typeof(Player), typeof(int), typeof(CardCreationOptions))]
public static class CardFactory_CreateForReward_Patch
{
    static bool Prefix(Player player, int cardCount, ref IEnumerable<CardCreationResult> __result)
    {
        if (!RetryMod.Enabled) return true;
        var forced = RetryContext.TargetCardChoices;
        if (forced == null || forced.Count == 0) return true;
        RetryContext.TargetCardChoices = null; // one-shot

        var list = new List<CardCreationResult>(cardCount);
        foreach (var sc in forced.Take(cardCount))
        {
            if (sc.Id == null) continue;
            if (ModelDb.GetByIdOrNull<CardModel>(sc.Id) == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}force card-reward: id {sc.Id} not in ModelDb — skipping");
                continue;
            }
            try
            {
                // IRunState.LoadCard restores the complete serialized
                // payload: upgrade level, enchantment (including amount),
                // SavedProperties, and FloorAddedToDeck. The returned card
                // belongs to the live run scope and is not added to the deck
                // until the player actually chooses it.
                var card = player.RunState.LoadCard(sc, player);
                list.Add(new CardCreationResult(card));
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}force card-reward: failed {sc.Id}: {ex.Message}");
            }
        }
        if (list.Count == 0) return true; // give up, let vanilla run
        GD.Print($"{RetryMod.LogPrefix}forced card reward: {string.Join(",", list.Select(r => r.Card?.Id.ToString()))}");
        __result = list;
        return false;
    }
}
