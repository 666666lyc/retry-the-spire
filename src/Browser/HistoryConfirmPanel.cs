// Adds a "select + confirm" affordance to the Run History screen,
// mirroring the act-map browser:
//   • Clicking a visited node selects it (the entry's hover highlight
//     stays applied; everyone else dims out).
//   • A confirm button — the real game NConfirmButton — slides in
//     from off-screen; pressing it kicks off the retry through the
//     same RetryRunner path the act-map browser uses.
//
// Previously a click immediately fired the retry, with no way to
// pull back. The select-first flow also lets us route through
// RetryRunner.NeedsAbandonPrompt while the user is still on Run
// History (rather than after a screen transition).
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace Retry;

internal static class HistoryConfirmPanel
{
    private static readonly FieldInfo? RunHistoryField = typeof(NMapPointHistoryEntry).GetField("_runHistory", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? EntryField = typeof(NMapPointHistoryEntry).GetField("_entry", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? PlayerField = typeof(NMapPointHistoryEntry).GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic);
    private static NMapPointHistoryEntry? _selected;
    private static Control? _confirmButton;
    private static NRunHistory? _attachedScreen;
    public static bool IsSelected(NMapPointHistoryEntry entry) => _selected == entry;
    // Called from NMapPointHistoryEntry_Ready_Patch when an entry is
    // clicked. Promotes that entry to "selected" — meaning we lock
    // its highlight in place (the OnUnfocus patch below preserves it)
    // and reveal the confirm button.
    public static void Select(NMapPointHistoryEntry entry)
    {
        if (_selected == entry)
        {
            // Re-clicking the selected entry just re-fires confirm
            // visibility (defensive — Enable tween is idempotent).
            ShowConfirm();
            return;
        }

        var prior = _selected;
        _selected = entry;
        try
        {
            if (prior != null && GodotObject.IsInstanceValid(prior))
            {
                prior.Unhighlight();
            }

            entry.Highlight();
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}HistoryConfirmPanel.Select: {ex.Message}");
        }

        EnsureConfirmAttached(entry);
        ShowConfirm();
    }

    public static void Clear()
    {
        try
        {
            if (_selected != null && GodotObject.IsInstanceValid(_selected))
            {
                _selected.Unhighlight();
            }
        }
        catch
        {
        }

        _selected = null;
        if (_confirmButton != null && GodotObject.IsInstanceValid(_confirmButton))
        {
            try
            {
                var disable = _confirmButton.GetType().GetMethod("Disable");
                disable?.Invoke(_confirmButton, null);
            }
            catch
            {
            }

            _confirmButton.Visible = false;
        }
    }

    private static void EnsureConfirmAttached(NMapPointHistoryEntry entry)
    {
        // Walk up to find the owning NRunHistory screen so the button
        // sticks to the screen rather than the entry node itself
        // (which is short-lived as the user scrolls through runs).
        Godot.Node? n = entry;
        NRunHistory? screen = null;
        while (n != null)
        {
            if (n is NRunHistory rh)
            {
                screen = rh;
                break;
            }

            n = n.GetParent();
        }

        if (screen == null)
            return;
        if (_attachedScreen == screen && _confirmButton != null && GodotObject.IsInstanceValid(_confirmButton) && _confirmButton.GetParent() == screen)
        {
            return;
        }

        // Tear down a stale button (e.g. left over from a prior screen).
        if (_confirmButton != null && GodotObject.IsInstanceValid(_confirmButton))
        {
            try
            {
                _confirmButton.QueueFreeSafely();
            }
            catch
            {
            }
        }

        _confirmButton = GameConfirmButton.Extract(OnConfirmPressed);
        if (_confirmButton == null)
            return;
        screen.AddChildSafely(_confirmButton);
        _attachedScreen = screen;
    }

    private static void ShowConfirm()
    {
        if (_confirmButton == null)
            return;
        _confirmButton.Visible = true;
        try
        {
            var enable = _confirmButton.GetType().GetMethod("Enable");
            enable?.Invoke(_confirmButton, null);
        }
        catch
        {
        }
    }

    private static void OnConfirmPressed()
    {
        if (_selected == null || !GodotObject.IsInstanceValid(_selected))
        {
            GD.PrintErr($"{RetryMod.LogPrefix}history confirm: no selected entry");
            return;
        }

        var entry = _selected;
        var history = RunHistoryField?.GetValue(entry) as RunHistory;
        var historyEntry = EntryField?.GetValue(entry) as MapPointHistoryEntry;
        var player = PlayerField?.GetValue(entry) as RunHistoryPlayer;
        int floor = entry.FloorNum;
        if (history == null || historyEntry == null || player == null)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}history confirm: missing entry data");
            return;
        }

        GD.Print($"{RetryMod.LogPrefix}history CONFIRM → retry floor={floor} seed={history.Seed}");
        // Don't pre-hide the button or clear selection — if the user
        // says No on the abandon prompt we want everything to look
        // exactly the way it did before they hit confirm. The modal
        // popup blocks input behind it, so double-clicks aren't a
        // worry. Re-apply Highlight after the call returns just in
        // case any focus juggling underneath the modal momentarily
        // killed the entry's hover tween.
        RetryRunner.OnHistoryNodeClicked(history, historyEntry, floor, player);
        try
        {
            if (GodotObject.IsInstanceValid(entry))
                entry.Highlight();
        }
        catch
        {
        }
    }
}

// Keep the selected entry's highlight applied no matter who calls
// Unhighlight. There are several callers we need to suppress:
//   • The entry's own OnUnfocus when the cursor leaves it.
//   • NMapPointHistory.UnHighlightEntries(), which iterates *every*
//     map entry and Unhighlights them all whenever the user un-hovers
//     a deck or relic history item. (This is the one that was wiping
//     our selection when the user mouse-overed anything else.)
//   • Any future callers we haven't found.
// Patching Unhighlight itself catches all of them in one shot, and
// it lets the original OnUnfocus run normally so the hover tooltip
// still gets removed.
[HarmonyPatch(typeof(NMapPointHistoryEntry), "Unhighlight")]
internal static class NMapPointHistoryEntry_Unhighlight_Patch
{
    static bool Prefix(NMapPointHistoryEntry __instance) => !HistoryConfirmPanel.IsSelected(__instance);
}

// When the user navigates to another run via the left/right arrows
// (RefreshAndSelectRun rebuilds the entries) we want to wipe the
// stale selection state so the confirm button doesn't refer to a
// freed entry node.
[HarmonyPatch(typeof(NRunHistory), "RefreshAndSelectRun")]
internal static class NRunHistory_RefreshAndSelectRun_Patch
{
    static void Prefix() => HistoryConfirmPanel.Clear();
}
