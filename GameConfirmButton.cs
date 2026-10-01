// Shared helper for lifting the game's NConfirmButton (blue banner +
// checkmark) out of an existing scene. The act-map browser and the
// run-history confirm panel both want this button — without sharing
// we'd have two copies of the orphan-instantiate dance.
//
// Each call extracts a *new* instance (the orphan scene is freed
// after) so the result can be reparented anywhere.
using System;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace Retry;

internal static class GameConfirmButton
{
    private static readonly string[] _candidateScenes = new[]
    {
        "res://scenes/screens/character_select_screen.tscn",
        "res://scenes/screens/custom_run/custom_run_load_screen.tscn",
        "res://scenes/screens/load_run_lobby.tscn",
    };

    public static Control? Extract(Action onPressed)
    {
        foreach (var path in _candidateScenes)
        {
            try
            {
                if (!ResourceLoader.Exists(path)) continue;
                var packed = ResourceLoader.Load<PackedScene>(path);
                if (packed == null) continue;
                var orphan = packed.Instantiate<Node>(PackedScene.GenEditState.Disabled);
                if (orphan == null) continue;
                var confirm = FindFirstNConfirmButton(orphan) as Control;
                if (confirm == null) { orphan.QueueFreeSafely(); continue; }
                confirm.GetParent()?.RemoveChild(confirm);
                orphan.QueueFreeSafely();
                confirm.Name = "RetryTheSpireConfirmBtn";
                confirm.Visible = false;
                confirm.Connect("Released", Callable.From<Node>(_ => onPressed()));
                return confirm;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}GameConfirmButton.Extract {path}: {ex.Message}");
            }
        }
        return null;
    }

    private static Node? FindFirstNConfirmButton(Node root)
    {
        if (root is MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton) return root;
        foreach (var ch in root.GetChildren())
        {
            var hit = FindFirstNConfirmButton(ch);
            if (hit != null) return hit;
        }
        return null;
    }
}
