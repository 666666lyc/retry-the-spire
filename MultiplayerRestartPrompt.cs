using System;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace Retry;

internal static class MultiplayerRestartPrompt
{
    public static void Show(string body, Action<bool> completed)
    {
        try
        {
            var popup = NAbandonRunConfirmPopup.Create(mainMenu: null)
                ?? throw new InvalidOperationException("popup scene could not be created");
            var modal = NModalContainer.Instance
                ?? throw new InvalidOperationException("modal container is unavailable");
            popup.Connect(Node.SignalName.Ready, Callable.From(() =>
            {
                var vertical = popup.GetNode<NVerticalPopup>("VerticalPopup");
                vertical.SetText("重打通信超时", body);
                vertical.DisconnectSignals();
                bool handled = false;
                void Finish(bool force)
                {
                    if (handled) return;
                    handled = true;
                    NModalContainer.Instance?.Clear();
                    completed(force);
                }
                vertical.InitNoButton(
                    new LocString("main_menu_ui", "GENERIC_POPUP.cancel"), _ => Finish(false));
                vertical.NoButton.SetText("取消重打");
                vertical.InitYesButton(
                    new LocString("main_menu_ui", "GENERIC_POPUP.confirm"), _ => Finish(true));
                vertical.YesButton.SetText("仍然重开");
            }), (uint)GodotObject.ConnectFlags.OneShot);
            modal.Add(popup);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}restart timeout prompt: {ex.Message}");
            // Safe default: a broken prompt must never destroy the live run.
            completed(false);
        }
    }
}
