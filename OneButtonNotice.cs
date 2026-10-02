using System;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace Retry;

/// <summary>
/// Small one-button notice built from the game's native vertical popup.
/// If the popup cannot be created, the continuation still runs so a warning
/// never turns into an accidental hard stop.
/// </summary>
public static class OneButtonNotice
{
    public static void Show(
        string title,
        string body,
        string buttonText,
        Action onAcknowledged)
    {
        bool completed = false;
        void ContinueOnce()
        {
            if (completed) return;
            completed = true;
            onAcknowledged();
        }

        try
        {
            var popup = NAbandonRunConfirmPopup.Create(mainMenu: null);
            if (popup == null) throw new InvalidOperationException("popup scene could not be created");
            var modal = NModalContainer.Instance
                ?? throw new InvalidOperationException("modal container is unavailable");
            popup.Connect(
                Node.SignalName.Ready,
                Callable.From(() => Initialize(popup, title, body, buttonText, ContinueOnce)),
                (uint)GodotObject.ConnectFlags.OneShot);
            modal.Add(popup);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}one-button notice: {ex.Message}");
            NModalContainer.Instance?.Clear();
            ContinueOnce();
        }
    }

    private static void Initialize(
        NAbandonRunConfirmPopup popup,
        string title,
        string body,
        string buttonText,
        Action onAcknowledged)
    {
        try
        {
            var vertical = popup.GetNode<NVerticalPopup>("VerticalPopup");
            vertical.SetText(title, body);
            vertical.DisconnectSignals();

            bool handled = false;
            vertical.InitYesButton(
                new LocString("main_menu_ui", "GENERIC_POPUP.confirm"),
                _ =>
                {
                    if (handled) return;
                    handled = true;
                    NModalContainer.Instance?.Clear();
                    onAcknowledged();
                });
            vertical.YesButton.SetText(buttonText);
            if (vertical.NoButton != null) vertical.NoButton.Visible = false;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}one-button notice init: {ex.Message}");
            NModalContainer.Instance?.Clear();
            onAcknowledged();
        }
    }
}
