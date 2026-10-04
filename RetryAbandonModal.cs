// Custom 3-option abandon confirmation for retry-mod entrypoints.
// Built by instantiating the game's NAbandonRunConfirmPopup scene
// (so we inherit its layout / styling / EnterTree animation) and
// then duplicating the YesButton to get a third NPopupYesNoButton.
//
// The third option is "Abandon (no save)" — skips writing the
// run_history entry so a flurry of test retries doesn't bloat the
// player's history. It uses an inline two-press confirm flow: first
// click re-labels the button to "Click again", second click within
// a few seconds fires for real. Reverts on timeout or on a click
// elsewhere in the popup.
//
// Why NOT NVerticalPopup direct: that only has YesButton/NoButton
// and there's no clean way to add a third without breaking the
// game's signal wiring on Close. Wrapping with the NAbandonRunConfirmPopup
// scene gives us its TreeExited cleanup for free.
using System;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace Retry;

public static class RetryAbandonModal
{
    public enum Choice { Cancel, AbandonSave, AbandonNoSave }

    public static void Show(
        string title,
        string body,
        Action<Choice> onChoice)
    {
        bool completed = false;
        void Complete(Choice choice, string reason)
        {
            if (completed) return;
            completed = true;
            GD.Print($"{RetryMod.LogPrefix}abandon modal choice={choice} reason={reason} title={title}");
            onChoice(choice);
        }

        try
        {
            var modal = NModalContainer.Instance;
            if (modal == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}abandon modal failed: no NModalContainer.Instance");
                Complete(Choice.Cancel, "missing-container");
                return;
            }
            if (modal.OpenModal != null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}abandon modal failed: another modal is already open title={title}");
                Complete(Choice.Cancel, "container-occupied");
                return;
            }

            // Wrap the game's standard abandon popup so we get its
            // intro animation + modal-clear glue for free, then mutate.
            var popup = NAbandonRunConfirmPopup.Create(mainMenu: null);
            if (popup == null)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}abandon modal failed: popup Create returned null (TestMode?)");
                Complete(Choice.Cancel, "create-failed");
                return;
            }

            GD.Print($"{RetryMod.LogPrefix}abandon modal creating title={title}");

            // Wire AFTER the popup's _Ready runs (which resolves
            // _verticalPopup and wires its default Yes/No handlers).
            // TreeEntered would be too early — it fires before _Ready.
            popup.Connect(Node.SignalName.Ready,
                Callable.From(() => InitAfterReady(popup, title, body, Complete)),
                (uint)GodotObject.ConnectFlags.OneShot);
            modal.Add(popup);

            // _Ready runs synchronously when the popup enters the tree. If
            // initialization failed, InitAfterReady already completed this
            // request as Cancel and cleared the modal.
            if (completed) return;

            // NModalContainer refuses Add while another modal is active.
            // It only logs a warning, so explicitly verify that our popup
            // was accepted instead of leaving the caller waiting forever.
            if (modal.OpenModal == null || popup.GetParent() == null || !popup.IsInsideTree())
            {
                GD.PrintErr($"{RetryMod.LogPrefix}abandon modal failed: container rejected popup title={title}");
                try { popup.QueueFree(); } catch { }
                Complete(Choice.Cancel, "container-rejected");
                return;
            }

            GD.Print($"{RetryMod.LogPrefix}abandon modal shown title={title}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}abandon modal failed: {ex.Message}");
            Complete(Choice.Cancel, "exception");
        }
    }

    private static void InitAfterReady(
        NAbandonRunConfirmPopup popup,
        string title,
        string body,
        Action<Choice, string> complete)
    {
        try
        {
            var vp = popup.GetNode<NVerticalPopup>("VerticalPopup");
            vp.SetText(title, body);

            // Disconnect popup's own _Ready-time wiring so our buttons
            // are the only callbacks. NAbandonRunConfirmPopup._Ready
            // already InitYesButton'd with its own handler; re-Init
            // here overwrites the text + adds extra connections.
            vp.DisconnectSignals();

            void Pick(Choice c)
            {
                complete(c, "user");
            }

            // Cancel (No) — left button. Standard "dismiss".
            vp.InitNoButton(new LocString("main_menu_ui", "GENERIC_POPUP.cancel"),
                _ => Pick(Choice.Cancel));

            // Abandon + save (Yes) — center button. Matches existing
            // 2-option flow exactly.
            vp.InitYesButton(new LocString("main_menu_ui", "GENERIC_POPUP.confirm"),
                _ => Pick(Choice.AbandonSave));

            // Add a third "Abandon (no save)" button by duplicating
            // the YesButton. Positioned to the right of YesButton.
            AddNoSaveButton(vp, () => Pick(Choice.AbandonNoSave));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}abandon modal init failed: {ex.Message}");
            complete(Choice.Cancel, "init-failed");
            try { NModalContainer.Instance?.Clear(); } catch { }
        }
    }

    private static void AddNoSaveButton(NVerticalPopup vp, Action onConfirmed)
    {
        var src = vp.YesButton;
        if (src == null || !GodotObject.IsInstanceValid(src)) return;

        // Duplicate with everything-but-signals so the dup keeps its
        // groups + script (= NPopupYesNoButton type) but starts with
        // no signal connections — our Released handler is the only one.
        const int dupFlags = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation);
        var dup = (NPopupYesNoButton)src.Duplicate(dupFlags);

        // Clone any Material BEFORE AddChild — NPopupYesNoButton._Ready
        // caches `_hsv = _image.GetMaterial()` and later OnFocus
        // mutates `_hsv` directly. If we clone after AddChild, _Ready
        // has already captured the original-shared material reference,
        // so hovering the dup still mutates the source button's
        // brightness (and vice versa). Cloning first means _Ready
        // captures the unique cloned material.
        CloneMaterials(dup);

        var parent = src.GetParent();
        parent.AddChild(dup);

        // Place ABOVE the YesButton (inside the popup) so it doesn't
        // hang off the right edge of the panel. Y offset is the
        // button's own height plus a small gap.
        dup.Name = "AbandonNoSaveButton";
        dup.IsYes = true;
        dup.SetText("Abandon (no save)");
        float buttonH = src.Size.Y > 0 ? src.Size.Y : 70f;
        dup.Position = src.Position + new Vector2(0f, -(buttonH + 20f));
        dup.Visible = true;

        bool armed = false;
        void Revert()
        {
            if (!GodotObject.IsInstanceValid(dup)) return;
            armed = false;
            dup.SetText("Abandon (no save)");
        }

        dup.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
        {
            if (!armed)
            {
                armed = true;
                dup.SetText("Click again to confirm");
                var tt = dup.GetTree()?.CreateTimer(3.0);
                tt?.Connect("timeout", Callable.From(Revert));
            }
            else
            {
                onConfirmed();
                NModalContainer.Instance?.Clear();
            }
        }));
    }

    // Recursively unique-ify any Material reference under `root` so
    // the duplicated subtree doesn't share shader-uniform state with
    // the source it was cloned from.
    private static void CloneMaterials(Node root)
    {
        if (root is CanvasItem ci && ci.Material != null)
            ci.Material = (Material)ci.Material.Duplicate();
        foreach (var child in root.GetChildren())
            CloneMaterials(child);
    }
}
