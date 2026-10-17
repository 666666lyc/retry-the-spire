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
    public static Control? Extract(Action onPressed) => Extract(onPressed, diagnostics: false);
    internal static Control? Extract(Action onPressed, bool diagnostics)
    {
        foreach (var path in _candidateScenes)
        {
            try
            {
                bool exists = ResourceLoader.Exists(path);
                if (diagnostics)
                    GD.Print($"{RetryMod.LogPrefix}ExtractConfirm probe: {path} exists={exists}");
                if (!exists)
                    continue;
                var packed = ResourceLoader.Load<PackedScene>(path);
                if (packed == null)
                {
                    if (diagnostics)
                        GD.Print($"{RetryMod.LogPrefix}ExtractConfirm: load returned null");
                    continue;
                }

                var orphan = packed.Instantiate<Node>(PackedScene.GenEditState.Disabled);
                if (orphan == null)
                {
                    if (diagnostics)
                        GD.Print($"{RetryMod.LogPrefix}ExtractConfirm: Instantiate null");
                    continue;
                }

                if (diagnostics)
                {
                    var names = new System.Text.StringBuilder();
                    DumpTree(orphan, names, 0, 3);
                    GD.Print($"{RetryMod.LogPrefix}ExtractConfirm tree of {path}:\n{names}");
                }

                if (FindFirstNConfirmButton(orphan)is not Control confirm)
                {
                    if (diagnostics)
                        GD.Print($"{RetryMod.LogPrefix}ExtractConfirm: no NConfirmButton in {path}");
                    orphan.QueueFreeSafely();
                    continue;
                }

                confirm.GetParent()?.RemoveChild(confirm);
                orphan.QueueFreeSafely();
                confirm.Name = "RetryTheSpireConfirmBtn";
                confirm.Visible = false;
                confirm.Connect("Released", Callable.From<Node>(_ => onPressed()));
                if (diagnostics)
                    GD.Print($"{RetryMod.LogPrefix}ExtractConfirm: extracted from {path}");
                return confirm;
            }
            catch (Exception ex)
            {
                string operation = diagnostics ? "ExtractGameConfirmButton" : "GameConfirmButton.Extract";
                GD.PrintErr($"{RetryMod.LogPrefix}{operation} {path}: {ex.Message}");
            }
        }

        return null;
    }

    private static void DumpTree(Node node, System.Text.StringBuilder output, int depth, int maxDepth)
    {
        if (depth > maxDepth)
            return;
        output.Append(new string (' ', depth * 2)).Append(node.GetType().Name).Append(":").Append(node.Name).Append('\n');
        foreach (var child in node.GetChildren())
            DumpTree(child, output, depth + 1, maxDepth);
    }

    private static Node? FindFirstNConfirmButton(Node root)
    {
        if (root is MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton)
            return root;
        foreach (var ch in root.GetChildren())
        {
            var hit = FindFirstNConfirmButton(ch);
            if (hit != null)
                return hit;
        }

        return null;
    }
}
