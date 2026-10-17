using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace Retry;

// While our browser is active, NRun.Instance must return the spawned
// overlay NRun (not the null CurrentRunNode). NMapScreen.Instance and
// most other in-run helpers route through NRun.Instance, so this one
// patch fixes their lookups too.
[HarmonyPatch(typeof(NRun), "get_Instance")]
public static class NRun_Instance_Patch
{
    static bool Prefix(ref NRun? __result)
    {
        if (NActMapBrowser.Active && NActMapBrowser.SpawnedNRun is { } nr)
        {
            __result = nr;
            return false;
        }

        return true;
    }
}

// During an MP browser session, return the selected historical
// player's id from NetSingleplayerGameService.NetId so
// RunManager.Launch's `LocalContext.NetId = NetService.NetId`
// resolves to a player that actually exists in the runState's player
// collection. Without this, RunStarted subscribers (NReactionWheel,
// etc) calling LocalContext.GetMe(playerCollection) throw "Local
// player not found in player collection." SP browser sessions keep
// the default 1uL.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Multiplayer.NetSingleplayerGameService), "get_NetId")]
public static class NetSingleplayerGameService_NetId_Patch
{
    static bool Prefix(ref ulong __result)
    {
        if (NActMapBrowser.NetServiceNetIdOverride is { } id)
        {
            __result = id;
            return false;
        }

        return true;
    }
}

// Force NRelicInventory.Add to use startsShown=true while the
// browser is active. Without this, InventoryInjector.AddRelicInternal
// fires the player's RelicObtained event → NRelicInventory.OnRelicObtained
// → Add(startsShown=false), which sets the icon's modulate alpha to
// zero and waits for a corresponding AnimateRelic call. That fade-in
// only happens during the live treasure / combat reward flows, so for
// our preview swap the relics stay invisible. The browser is purely
// visual, so the "fade in on grab" animation isn't meaningful — we
// just want every relic shown immediately.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory), "Add")]
public static class NRelicInventory_Add_Patch
{
    static void Prefix(ref bool startsShown)
    {
        if (NActMapBrowser.Active)
            startsShown = true;
    }
}

// Intercept node clicks while the browser is active.
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.OnMapPointSelectedLocally))]
public static class NMapScreen_OnMapPointSelectedLocally_Patch
{
    static bool Prefix(NMapPoint point)
    {
        if (NActMapBrowser.Active)
        {
            NActMapBrowser.HandleMapPointClick(point.Point.coord);
            return false;
        }

        return true;
    }
}

// If the user goes via the in-run pause menu's Save & Quit (which calls
// NGame.ReturnToMainMenu), make sure we tear down our overlay first so
// the menu can land cleanly on NMainMenu.
[HarmonyPatch(typeof(NGame), nameof(NGame.ReturnToMainMenu))]
public static class NGame_ReturnToMainMenu_Patch
{
    static void Prefix()
    {
        if (NActMapBrowser.Active)
            NActMapBrowser.Close();
    }
}

// Force-hide the speedrun timer while the browser is up. NRunTimer's
// RefreshVisibility fires on NMapScreen.VisibilityChanged and re-shows
// the timer if PrefsSave.ShowRunTimer is on — so a one-shot
// topBar.Timer.Visible = false from TrimTopBar gets clobbered.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.TopBar.NRunTimer), "ToggleTimer")]
public static class NRunTimer_ToggleTimer_Patch
{
    static bool Prefix(MegaCrit.Sts2.Core.Nodes.TopBar.NRunTimer __instance, ref bool on)
    {
        if (NActMapBrowser.Active)
        {
            on = false; // force-hidden while browsing
        }

        return true;
    }
}

// When browser is active, route the TopBar Pause button to the
// out-of-run Settings screen instead of NPauseMenu (the in-run
// version exposes Save & Quit / Give Up which are nonsensical here).
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarPauseButton), "OnRelease")]
public static class NTopBarPauseButton_OnRelease_Patch
{
    static bool Prefix(MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarPauseButton __instance)
    {
        if (!NActMapBrowser.Active)
            return true;
        try
        {
            var stack = NRun.Instance?.GlobalUi?.SubmenuStack;
            stack?.ShowScreen(MegaCrit.Sts2.Core.Nodes.Screens.CapstoneSubmenuType.Settings);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}pause→settings: {ex.Message}");
        }

        return false;
    }
}

// While the browser is up, NNormalMapPoint._Process drives a sinusoidal
// "I'm selectable" pulse on every Travelable node (anything visited,
// because we turn on debug travel). For preview UX we want only the
// node the user has clicked to pulse — everyone else should sit still.
[HarmonyPatch]
public static class NNormalMapPoint_Process_Patch
{
    private static readonly FieldInfo? IconContainerField = typeof(NNormalMapPoint).GetField("_iconContainer", BindingFlags.Instance | BindingFlags.NonPublic);
    static MethodBase TargetMethod() => AccessTools.Method(typeof(NNormalMapPoint), "_Process", new[] { typeof(double) });
    static bool Prefix(NNormalMapPoint __instance)
    {
        if (!NActMapBrowser.Active)
            return true;
        var sel = NActMapBrowser.SelectedCoord;
        bool isSelected = sel is MapCoord sc && __instance.Point.coord.row == sc.row && __instance.Point.coord.col == sc.col;
        if (isSelected)
            return true; // run original pulse logic
        // Force the icon back to its rest scale and skip _Process so the
        // sin-wave from the original doesn't run.
        if (IconContainerField?.GetValue(__instance)is Control ic)
            ic.Scale = Vector2.One;
        return false;
    }
}

// Original NPotionHolder.DiscardPotion runs a 0.4s slide-up tween,
// which during preview swaps leaves the outgoing potions sliding away
// while the new potions have already snapped into their slots. Replace
// with a quick fade-out so the cross-fade reads as a single swap.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder), "DiscardPotion")]
public static class NPotionHolder_DiscardPotion_Patch
{
    private static readonly System.Reflection.MethodInfo? PotionSetter = AccessTools.PropertySetter(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder), "Potion");
    private static readonly FieldInfo? EmptyIconField = typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder).GetField("_emptyIcon", BindingFlags.Instance | BindingFlags.NonPublic);
    // Park the empty-icon fade-in tween in the holder's own
    // _emptyPotionTween slot. That's the field the original AddPotion
    // calls `?.Kill()` on, so when a fresh potion lands in this slot
    // a beat later, the lingering fade is cancelled — otherwise it
    // keeps lerping the placeholder back to White and we see both the
    // empty icon and the new potion stacked.
    private static readonly FieldInfo? EmptyPotionTweenField = typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder).GetField("_emptyPotionTween", BindingFlags.Instance | BindingFlags.NonPublic);
    static bool Prefix(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder __instance)
    {
        if (!NActMapBrowser.Active)
            return true;
        try
        {
            var potion = __instance.Potion;
            if (potion == null)
                return true;
            PotionSetter?.Invoke(__instance, new object? [] { null });
            // Tween 1: fade the outgoing potion to transparent + remove.
            var potionTween = __instance.CreateTween();
            potionTween.TweenProperty(potion, "modulate", Colors.Transparent, 0.12);
            potionTween.TweenCallback(Callable.From(() =>
            {
                try
                {
                    __instance.RemoveChildSafely(potion);
                    potion.QueueFreeSafely();
                }
                catch
                {
                }
            }));
            // Tween 2: cross-fade the empty placeholder in. Stored in
            // _emptyPotionTween so AddPotion's Kill() takes care of it.
            if (EmptyIconField?.GetValue(__instance)is TextureRect emptyIcon)
            {
                if (EmptyPotionTweenField?.GetValue(__instance)is Tween prior)
                {
                    try
                    {
                        prior.Kill();
                    }
                    catch
                    {
                    }
                }

                var emptyTween = __instance.CreateTween();
                emptyTween.TweenProperty(emptyIcon, "modulate", Colors.White, 0.15);
                EmptyPotionTweenField?.SetValue(__instance, emptyTween);
            }

            return false;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}potion fast-discard: {ex.Message}");
            return true;
        }
    }
}

// Pair: short fade-in on AddPotion so swapped-in potions don't pop
// instantly while the outgoing ones are still fading out.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder), "AddPotion")]
public static class NPotionHolder_AddPotion_Patch
{
    static void Postfix(MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder __instance)
    {
        if (!NActMapBrowser.Active)
            return;
        try
        {
            var p = __instance.Potion;
            if (p == null)
                return;
            p.Modulate = Colors.Transparent;
            var tween = __instance.CreateTween();
            tween.TweenProperty(p, "modulate", Colors.White, 0.15);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}potion fade-in: {ex.Message}");
        }
    }
}
