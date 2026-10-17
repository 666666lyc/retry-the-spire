using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Retry;

internal static class RoomReload
{
    internal static async Task RestartSingleplayerAsync(RunState restored, SerializableRun save)
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("NGame.Instance is unavailable");
        try
        {
            await game.Transition.FadeOut(0.2f);
        }
        catch
        {
        }

        RetryContext.ResetAll();
        RunManager.Instance.CleanUp(graceful: true);
        QuickRoomRestart._liveRunCleaned = true;
        await RunManager.Instance.SetUpSavedSingleplayer(restored, save);
        await game.LoadRun(restored, save.PreFinishedRoom);
        try
        {
            await game.Transition.FadeIn(0.2f);
        }
        catch
        {
        }

        GD.Print($"{RetryMod.LogPrefix}quick restart: single-player reload complete");
        QuickRoomRestart._restartInProgress = false;
    }

    internal static async Task RestartPreparedMultiplayerAsync(SerializableRun save, ulong hostId)
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("NGame.Instance is unavailable");
        try
        {
            await game.Transition.FadeOut(0.2f);
        }
        catch
        {
        }

        RetryContext.ResetAll();
        // Split combat and network cleanup deliberately. A graceful Steam host
        // shutdown delays closing its listen socket; starting the replacement
        // host during that delay lets the old cleanup close the new listener.
        // RunManager.CleanUp(false), however, skips CombatManager.Reset and
        // leaves the old combat state installed, so a combat-room reload can
        // fail in SetUpCombat and tear down the newly created room.
        CombatManager.Instance.Reset(graceful: true);
        RunManager.Instance.CleanUp(graceful: false);
        QuickRoomRestart._liveRunCleaned = true;
        LocalContext.NetId = hostId;
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        var menu = NMainMenu.Create(openTimeline: false);
        game.RootSceneContainer?.SetCurrentScene(menu);
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        var submenu = menu.OpenMultiplayerSubmenu();
        MultiplayerRestartCoordinator.ArmHostAutoStart();
        submenu.StartHost(save);
        try
        {
            await game.Transition.FadeIn(0.2f);
        }
        catch
        {
        }

        GD.Print($"{RetryMod.LogPrefix}quick restart: multiplayer load lobby requested");
        QuickRoomRestart._restartInProgress = false;
    }

    internal static async Task RestartPreparedMultiplayerSafelyAsync(SerializableRun save, ulong hostId)
    {
        try
        {
            await RestartPreparedMultiplayerAsync(save, hostId);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}quick restart failed: {ex.Message}\n{ex.StackTrace}");
            if (QuickRoomRestart._liveRunCleaned)
            {
                await EnsureMainMenuAfterFailureAsync();
                QuickRoomRestart.ShowError("重打加载失败，但本关入口存档仍然保留。请从主菜单继续游戏。", resetGuard: true);
            }
            else
            {
                QuickRoomRestart.ShowError($"重打加载失败：{ex.Message}", resetGuard: true);
            }
        }
    }

    internal static async Task RestartPreparedDirectMultiplayerSafelyAsync(SerializableRun save, RunState restored)
    {
        try
        {
            await RestartPreparedDirectMultiplayerAsync(save, restored);
            MultiplayerRestartCoordinator.DirectLoadCompleted();
            QuickRoomRestart._restartInProgress = false;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}quick restart direct load failed: {ex.Message}\n{ex.StackTrace}");
            MultiplayerRestartCoordinator.DirectLoadFailed();
            QuickRoomRestart._restartInProgress = false;
            QuickRoomRestart._preparedMultiplayerSave = null;
            QuickRoomRestart._preparedMultiplayerState = null;
            QuickRoomRestart._preparedMultiplayerHostId = 0;
            try
            {
                var manager = RunManager.Instance;
                var net = manager?.NetService;
                if (manager?.IsInProgress == true)
                    manager.CleanUp(graceful: false);
                if (net?.IsConnected == true)
                    net.Disconnect(NetError.Quit, false);
            }
            catch
            {
            }

            await EnsureMainMenuAfterFailureAsync();
            OneButtonNotice.Show("重打", "多人直载失败，当前联机连接已关闭。入口存档仍然保留，请从主菜单继续游戏。", "知道了", () =>
            {
            });
        }
    }

    internal static async Task RestartPreparedDirectMultiplayerAsync(SerializableRun save, RunState restored)
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("NGame.Instance is unavailable");
        var manager = RunManager.Instance ?? throw new InvalidOperationException("RunManager.Instance is unavailable");
        var net = manager.NetService ?? throw new InvalidOperationException("multiplayer service is unavailable");
        var oldLobby = manager.RunLobby ?? throw new InvalidOperationException("active run lobby is unavailable");
        if (!net.IsConnected)
            throw new InvalidOperationException("multiplayer service disconnected before direct reload");
        if (QuickRoomRestart.RunLobbyBackingField == null)
            throw new MissingFieldException(typeof(RunManager).FullName, "<RunLobby>k__BackingField");
        try
        {
            await game.Transition.FadeOut(0.2f);
        }
        catch
        {
        }

        // Match LoadRunLobby.BeginRunLocally: hold gameplay packets until
        // RunManager.Launch finishes installing the restored run.
        net.SetBufferMessages(true);
        RetryContext.ResetAll();
        CombatManager.Instance.Reset(graceful: true);
        // CleanUp always disconnects NetService, even when RunLobby is null.
        // Hide the old lobby so CleanUp cannot dispose it, and suppress only
        // the synchronous Disconnect call made by this specific cleanup. This
        // retains the normal RunManager/subsystem cleanup without closing the
        // Steam connection. Afterwards dispose only the old lobby handlers.
        QuickRoomRestart.RunLobbyBackingField.SetValue(manager, null);
        CleanUpRunPreservingNetwork(manager);
        QuickRoomRestart._liveRunCleaned = true;
        oldLobby.Dispose();
        if (!net.IsConnected)
            throw new InvalidOperationException("multiplayer service disconnected during old-run cleanup");
        LocalContext.NetId = net.NetId;
        var loadLobby = new LoadRunLobby(net, DirectLoadRunLobbyListener.Instance, save);
        bool loadLobbyCleaned = false;
        try
        {
            await manager.SetUpSavedMultiplayer(restored, loadLobby);
            if (!net.IsConnected)
                throw new InvalidOperationException("multiplayer service disconnected during direct reload");
            await game.LoadRun(restored, save.PreFinishedRoom);
            loadLobby.CleanUp(false, NetError.Quit);
            loadLobbyCleaned = true;
        }
        finally
        {
            // On success the input synchronizer now belongs to RunManager, so
            // cleanup must only unregister temporary lobby handlers. On failure
            // disconnect the half-installed multiplayer session.
            if (!loadLobbyCleaned)
            {
                try
                {
                    loadLobby.CleanUp(true, NetError.Quit);
                }
                catch
                {
                }
            }
        }

        try
        {
            await game.Transition.FadeIn(0.2f);
        }
        catch
        {
        }

        GD.Print($"{RetryMod.LogPrefix}quick restart: multiplayer direct reload complete " + $"lobby={SafeLobbyIdentifier(net) ?? "?"}");
    }

    internal static void CleanUpRunPreservingNetwork(RunManager manager)
    {
        QuickRoomRestart._networkPreservingCleanupDepth++;
        try
        {
            manager.CleanUp(graceful: false);
        }
        finally
        {
            QuickRoomRestart._networkPreservingCleanupDepth--;
        }
    }

    internal static string? SafeLobbyIdentifier(INetGameService net)
    {
        try
        {
            return net.GetRawLobbyIdentifier();
        }
        catch
        {
            return null;
        }
    }

    internal static async Task EnsureMainMenuAfterFailureAsync()
    {
        try
        {
            var game = NGame.Instance;
            if (game == null)
                return;
            if (game.RootSceneContainer?.CurrentScene is NMainMenu)
                return;
            game.RootSceneContainer?.SetCurrentScene(NMainMenu.Create(openTimeline: false));
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
            try
            {
                await game.Transition.FadeIn(0.2f);
            }
            catch
            {
            }
        }
        catch
        {
        }
    }
}
