using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.addons.mega_text;

namespace Retry;

[HarmonyPatch(typeof(NPauseMenu), "_Ready")]
public static class NPauseMenu_QuickRestart_Patch
{
    private const string RestartButtonName = "RetryTheSpireQuickRoomRestartButton";
    private const string EncounterButtonName = "RetryTheSpireRerollEncounterButton";
    private const string ShuffleButtonName = "RetryTheSpireRerollShuffleButton";
    private static readonly FieldInfo? ResumeButtonField = typeof(NPauseMenu).GetField("_resumeButton", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? SettingsButtonField = typeof(NPauseMenu).GetField("_settingsButton", BindingFlags.Instance | BindingFlags.NonPublic);
    private sealed class MenuButtonState
    {
        internal required NPauseMenu Menu { get; init; }
        internal required NPauseMenuButton Resume { get; init; }
        internal required NPauseMenuButton Settings { get; init; }
        internal required NPauseMenuButton Restart { get; init; }
        internal required NPauseMenuButton Encounter { get; init; }
        internal required NPauseMenuButton Shuffle { get; init; }
        internal required Node Parent { get; init; }
        internal required Dictionary<NPauseMenuButton, Vector2> OriginalPositions { get; init; }
        internal required Vector2 SettingsPosition { get; init; }
        internal required float Spacing { get; init; }
        internal required Control.FocusModeEnum VisibleFocusMode { get; init; }
    }

    static void Postfix(NPauseMenu __instance)
    {
        if (!RetryMod.Enabled)
            return;
        try
        {
            if (__instance.FindChild(RestartButtonName, recursive: true, owned: false) != null)
                return;
            var resume = ResumeButtonField?.GetValue(__instance) as NPauseMenuButton;
            var settings = SettingsButtonField?.GetValue(__instance) as NPauseMenuButton;
            if (resume == null || settings == null)
                return;
            const int duplicateFlags = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation);
            var specs = new (string Name, string Text, string Tooltip, RestartMode Mode)[]
            {
                (RestartButtonName, "重打", "从本关入口存档重新开始", RestartMode.Exact),
                (EncounterButtonName, "遭遇重掷", "更换同层级遭遇并重新开始战斗", RestartMode.RerollEncounter),
                (ShuffleButtonName, "牌序重掷", "重掷洗牌顺序并重新开始战斗", RestartMode.RerollShuffle),
            };
            var buttons = new List<NPauseMenuButton>(specs.Length);
            foreach (var spec in specs)
            {
                var button = (NPauseMenuButton)resume.Duplicate(duplicateFlags);
                CloneMaterials(button);
                button.Name = spec.Name;
                SetButtonText(button, spec.Text);
                button.TooltipText = spec.Tooltip;
                button.Visible = false;
                RestartMode mode = spec.Mode;
                button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => QuickRoomRestart.Begin(mode)));
                buttons.Add(button);
            }

            var parent = resume.GetParent();
            if (parent == null)
                return;
            var settingsPosition = settings.Position;
            var originalPositions = parent.GetChildren().OfType<NPauseMenuButton>().ToDictionary(button => button, button => button.Position);
            foreach (var button in buttons)
                parent.AddChild(button);
            if (parent is Container)
            {
                int insertAt = resume.GetIndex() + 1;
                for (int i = 0; i < buttons.Count; i++)
                    parent.MoveChild(buttons[i], Math.Min(insertAt + i, parent.GetChildCount() - 1));
            }

            float spacing = settingsPosition.Y - resume.Position.Y;
            if (spacing < 4f)
                spacing = Math.Max(resume.Size.Y + 20f, 90f);
            var state = new MenuButtonState
            {
                Menu = __instance,
                Resume = resume,
                Settings = settings,
                Restart = buttons[0],
                Encounter = buttons[1],
                Shuffle = buttons[2],
                Parent = parent,
                OriginalPositions = originalPositions,
                SettingsPosition = settingsPosition,
                Spacing = spacing,
                VisibleFocusMode = resume.FocusMode,
            };
            __instance.Connect(CanvasItem.SignalName.VisibilityChanged, Callable.From(() => RefreshButtons(state)));
            RefreshButtons(state);
            GD.Print($"{RetryMod.LogPrefix}quick restart buttons added to pause menu count=3");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}pause quick restart button: {ex.Message}");
        }
    }

    private static void RefreshButtons(MenuButtonState state)
    {
        if (!GodotObject.IsInstanceValid(state.Menu))
            return;
        bool canRestart = QuickRoomRestart.CanRestart(out _);
        bool canReroll = canRestart && QuickRoomRestart.CanRerollCombat;
        SetVisibleAndFocusable(state.Restart, canRestart, state.VisibleFocusMode);
        SetVisibleAndFocusable(state.Encounter, canReroll, state.VisibleFocusMode);
        SetVisibleAndFocusable(state.Shuffle, canReroll, state.VisibleFocusMode);
        var visibleButtons = new List<NPauseMenuButton>(3);
        if (state.Restart.Visible)
            visibleButtons.Add(state.Restart);
        if (state.Encounter.Visible)
            visibleButtons.Add(state.Encounter);
        if (state.Shuffle.Visible)
            visibleButtons.Add(state.Shuffle);
        if (state.Parent is not Container)
        {
            foreach (var(button, originalPosition)in state.OriginalPositions)
            {
                if (button == state.Resume)
                    continue;
                button.Position = originalPosition.Y >= state.SettingsPosition.Y - 1f ? originalPosition + new Vector2(0f, state.Spacing * visibleButtons.Count) : originalPosition;
            }

            for (int i = 0; i < visibleButtons.Count; i++)
                visibleButtons[i].Position = state.SettingsPosition + new Vector2(0f, state.Spacing * i);
        }

        var focusChain = new List<NPauseMenuButton>(visibleButtons.Count + 2)
        {
            state.Resume,
        };
        focusChain.AddRange(visibleButtons);
        focusChain.Add(state.Settings);
        for (int i = 0; i < focusChain.Count - 1; i++)
        {
            var upper = focusChain[i];
            var lower = focusChain[i + 1];
            upper.FocusNeighborBottom = upper.GetPathTo(lower);
            upper.FocusNext = upper.GetPathTo(lower);
            lower.FocusNeighborTop = lower.GetPathTo(upper);
            lower.FocusPrevious = lower.GetPathTo(upper);
        }

        GD.Print($"{RetryMod.LogPrefix}pause restart buttons refreshed " + $"restart={canRestart} rerolls={canReroll}");
    }

    private static void SetVisibleAndFocusable(NPauseMenuButton button, bool visible, Control.FocusModeEnum visibleFocusMode)
    {
        button.Visible = visible;
        button.FocusMode = visible ? visibleFocusMode : Control.FocusModeEnum.None;
    }

    private static void SetButtonText(Node root, string text)
    {
        if (root is MegaLabel megaLabel)
        {
            megaLabel.Text = text;
            return;
        }

        if (root is Label label)
        {
            label.Text = text;
            return;
        }

        foreach (Node child in root.GetChildren())
            SetButtonText(child, text);
    }

    private static void CloneMaterials(Node root)
    {
        if (root is CanvasItem item && item.Material != null)
            item.Material = (Material)item.Material.Duplicate();
        foreach (Node child in root.GetChildren())
            CloneMaterials(child);
    }
}
