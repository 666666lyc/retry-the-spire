using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.addons.mega_text;

namespace Retry;

/// <summary>
/// Reloads the native room-entry save without first writing the current
/// in-room state. Single-player loads directly; a multiplayer host returns
/// to a native load-run lobby so unmodded clients can rejoin safely.
/// </summary>
public static class QuickRoomRestart
{
    private static bool _restartInProgress;
    private static bool _liveRunCleaned;
    private static SerializableRun? _preparedMultiplayerSave;
    private static ulong _preparedMultiplayerHostId;

    public static bool CanRestart(out bool multiplayer)
    {
        multiplayer = false;
        try
        {
            var manager = RunManager.Instance;
            if (manager == null || !manager.IsInProgress) return false;

            multiplayer = !manager.IsSingleplayerOrFakeMultiplayer;
            if (!multiplayer)
                return SaveManager.Instance?.HasRunSave == true;

            return manager.NetService is INetHostGameService
                && SaveManager.Instance?.HasMultiplayerRunSave == true;
        }
        catch
        {
            return false;
        }
    }

    public static void Begin()
    {
        if (_restartInProgress) return;
        _restartInProgress = true;
        _liveRunCleaned = false;
        if (CanRestart(out bool multiplayer) && multiplayer)
            _ = PrepareMultiplayerRestartAsync();
        else
            _ = RestartAsync();
    }

    internal static void ContinueAfterPreparation()
    {
        var save = _preparedMultiplayerSave;
        ulong hostId = _preparedMultiplayerHostId;
        _preparedMultiplayerSave = null;
        _preparedMultiplayerHostId = 0;
        if (save == null || hostId == 0)
        {
            ShowError("多人续局存档尚未准备完成，已取消本次重打。", resetGuard: true);
            return;
        }
        _ = RestartPreparedMultiplayerSafelyAsync(save, hostId);
    }

    internal static void CancelPreparation()
    {
        _restartInProgress = false;
        _liveRunCleaned = false;
        _preparedMultiplayerSave = null;
        _preparedMultiplayerHostId = 0;
    }

    private static async Task PrepareMultiplayerRestartAsync()
    {
        try
        {
            if (!CanRestart(out bool multiplayer) || !multiplayer)
            {
                ShowError("当前对局没有可用的多人本关入口存档，无法重打。", resetGuard: true);
                return;
            }

            var saveManager = SaveManager.Instance;
            if (saveManager?.CurrentRunSaveTask is { } pendingSave)
                await pendingSave;

            ulong hostId = LocalContext.NetId ?? 0;
            if (hostId == 0)
            {
                ShowError("无法确定房主玩家 ID，不能安全重建多人续局大厅。", resetGuard: true);
                return;
            }

            var read = saveManager!.LoadAndCanonicalizeMultiplayerRunSave(hostId);
            if (!read.Success || read.SaveData == null)
            {
                ShowError($"读取多人入口存档失败：{read.Status}", resetGuard: true);
                return;
            }

            _preparedMultiplayerSave = read.SaveData;
            _preparedMultiplayerHostId = hostId;
            GD.Print($"{RetryMod.LogPrefix}quick restart: multiplayer room-entry save prepared before handshake");
            MultiplayerRestartCoordinator.BeginHostPreparation();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}quick restart preflight failed: {ex.Message}\n{ex.StackTrace}");
            ShowError($"重打准备失败：{ex.Message}", resetGuard: true);
        }
    }

    private static async Task RestartAsync()
    {
        try
        {
            if (!CanRestart(out bool multiplayer))
            {
                ShowError("当前对局没有可用的本关入口存档，无法重打。", resetGuard: true);
                return;
            }

            var saveManager = SaveManager.Instance;
            if (saveManager?.CurrentRunSaveTask is { } pendingSave)
                await pendingSave;

            if (!multiplayer)
            {
                var read = saveManager!.LoadRunSave();
                if (!read.Success || read.SaveData == null)
                {
                    ShowError($"读取单人入口存档失败：{read.Status}", resetGuard: true);
                    return;
                }

                // Deserialize before touching the live run. A corrupt save
                // must never turn a recoverable battle into a forced exit.
                RunState restored;
                try { restored = RunState.FromSerializable(read.SaveData); }
                catch (Exception ex)
                {
                    ShowError($"入口存档无法还原：{ex.Message}", resetGuard: true);
                    return;
                }

                GD.Print($"{RetryMod.LogPrefix}quick restart: single-player room-entry save loaded");
                await RestartSingleplayerAsync(restored, read.SaveData);
            }
            else
            {
                ShowError("多人重打准备状态异常，已取消本次重打。", resetGuard: true);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}quick restart failed: {ex.Message}\n{ex.StackTrace}");
            if (_liveRunCleaned)
            {
                await EnsureMainMenuAfterFailureAsync();
                ShowError("重打加载失败，但本关入口存档仍然保留。请从主菜单继续游戏。", resetGuard: true);
            }
            else
            {
                ShowError($"重打加载失败：{ex.Message}", resetGuard: true);
            }
        }
    }

    private static async Task RestartSingleplayerAsync(
        RunState restored,
        SerializableRun save)
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("NGame.Instance is unavailable");
        try { await game.Transition.FadeOut(0.2f); } catch { }

        RetryContext.ResetAll();
        RunManager.Instance.CleanUp(graceful: true);
        _liveRunCleaned = true;

        await RunManager.Instance.SetUpSavedSingleplayer(restored, save);
        await game.LoadRun(restored, save.PreFinishedRoom);
        try { await game.Transition.FadeIn(0.2f); } catch { }
        GD.Print($"{RetryMod.LogPrefix}quick restart: single-player reload complete");
        _restartInProgress = false;
    }

    private static async Task RestartPreparedMultiplayerAsync(
        SerializableRun save,
        ulong hostId)
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("NGame.Instance is unavailable");
        try { await game.Transition.FadeOut(0.2f); } catch { }

        RetryContext.ResetAll();
        // A graceful Steam host shutdown delays closing its listen socket.
        // Starting the replacement host during that delay lets the old cleanup
        // close the new listener, leaving a visible but unreachable lobby.
        RunManager.Instance.CleanUp(graceful: false);
        _liveRunCleaned = true;
        LocalContext.NetId = hostId;
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);

        var menu = NMainMenu.Create(openTimeline: false);
        game.RootSceneContainer?.SetCurrentScene(menu);
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);

        var submenu = menu.OpenMultiplayerSubmenu();
        MultiplayerRestartCoordinator.ArmHostAutoStart();
        submenu.StartHost(save);
        try { await game.Transition.FadeIn(0.2f); } catch { }
        GD.Print($"{RetryMod.LogPrefix}quick restart: multiplayer load lobby requested");
        _restartInProgress = false;
    }

    private static async Task RestartPreparedMultiplayerSafelyAsync(
        SerializableRun save,
        ulong hostId)
    {
        try
        {
            await RestartPreparedMultiplayerAsync(save, hostId);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}quick restart failed: {ex.Message}\n{ex.StackTrace}");
            if (_liveRunCleaned)
            {
                await EnsureMainMenuAfterFailureAsync();
                ShowError("重打加载失败，但本关入口存档仍然保留。请从主菜单继续游戏。", resetGuard: true);
            }
            else
            {
                ShowError($"重打加载失败：{ex.Message}", resetGuard: true);
            }
        }
    }

    private static async Task EnsureMainMenuAfterFailureAsync()
    {
        try
        {
            var game = NGame.Instance;
            if (game == null) return;
            if (game.RootSceneContainer?.CurrentScene is NMainMenu) return;
            game.RootSceneContainer?.SetCurrentScene(NMainMenu.Create(openTimeline: false));
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
            try { await game.Transition.FadeIn(0.2f); } catch { }
        }
        catch { }
    }

    private static void ShowError(string body, bool resetGuard)
    {
        GD.PrintErr($"{RetryMod.LogPrefix}quick restart: {body}");
        if (resetGuard)
        {
            if (!_liveRunCleaned) MultiplayerRestartCoordinator.AbortBeforeDisconnect();
            _restartInProgress = false;
            _preparedMultiplayerSave = null;
            _preparedMultiplayerHostId = 0;
        }
        OneButtonNotice.Show("重打", body, "知道了", () => { });
    }
}

[HarmonyPatch(typeof(NPauseMenu), "_Ready")]
public static class NPauseMenu_QuickRestart_Patch
{
    private const string ButtonName = "RetryTheSpireQuickRoomRestartButton";

    private static readonly FieldInfo? ResumeButtonField = typeof(NPauseMenu).GetField(
        "_resumeButton", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? SettingsButtonField = typeof(NPauseMenu).GetField(
        "_settingsButton", BindingFlags.Instance | BindingFlags.NonPublic);

    static void Postfix(NPauseMenu __instance)
    {
        if (!RetryMod.Enabled) return;
        try
        {
            if (!QuickRoomRestart.CanRestart(out _)) return;
            if (__instance.FindChild(ButtonName, recursive: true, owned: false) != null) return;

            var resume = ResumeButtonField?.GetValue(__instance) as NPauseMenuButton;
            var settings = SettingsButtonField?.GetValue(__instance) as NPauseMenuButton;
            if (resume == null || settings == null) return;

            const int duplicateFlags = (int)(Node.DuplicateFlags.Groups
                | Node.DuplicateFlags.Scripts
                | Node.DuplicateFlags.UseInstantiation);
            var button = (NPauseMenuButton)resume.Duplicate(duplicateFlags);
            CloneMaterials(button);
            button.Name = ButtonName;

            var parent = resume.GetParent();
            if (parent == null) return;
            var settingsPosition = settings.Position;
            parent.AddChild(button);

            if (parent is Container)
            {
                parent.MoveChild(button, Math.Min(resume.GetIndex() + 1, parent.GetChildCount() - 1));
            }
            else
            {
                float spacing = settingsPosition.Y - resume.Position.Y;
                if (spacing < 4f) spacing = Math.Max(resume.Size.Y + 20f, 90f);
                foreach (var child in parent.GetChildren().OfType<NPauseMenuButton>())
                {
                    if (child == resume || child == button) continue;
                    if (child.Position.Y >= settingsPosition.Y - 1f)
                        child.Position += new Vector2(0f, spacing);
                }
                button.Position = settingsPosition;
            }

            SetButtonText(button, "重打");
            button.TooltipText = "从本关入口存档重新开始";
            button.Visible = true;
            button.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ => QuickRoomRestart.Begin()));

            // Include the injected control in keyboard/controller traversal.
            resume.FocusNeighborBottom = resume.GetPathTo(button);
            resume.FocusNext = resume.GetPathTo(button);
            button.FocusNeighborTop = button.GetPathTo(resume);
            button.FocusPrevious = button.GetPathTo(resume);
            button.FocusNeighborBottom = button.GetPathTo(settings);
            button.FocusNext = button.GetPathTo(settings);
            settings.FocusNeighborTop = settings.GetPathTo(button);
            settings.FocusPrevious = settings.GetPathTo(button);

            GD.Print($"{RetryMod.LogPrefix}quick restart button added to pause menu");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}pause quick restart button: {ex.Message}");
        }
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
        foreach (Node child in root.GetChildren()) SetButtonText(child, text);
    }

    private static void CloneMaterials(Node root)
    {
        if (root is CanvasItem item && item.Material != null)
            item.Material = (Material)item.Material.Duplicate();
        foreach (Node child in root.GetChildren()) CloneMaterials(child);
    }
}
