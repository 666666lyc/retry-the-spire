using System;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Retry;

internal enum RestartMode
{
    Exact,
    RerollEncounter,
    RerollShuffle,
}

/// <summary>
/// Reloads the native room-entry save without first writing the current
/// in-room state. Single-player loads directly. Multiplayer loads in place
/// when every connected peer supports it, otherwise it returns to the native
/// load-run lobby so unmodded clients can rejoin safely.
/// </summary>
public static class QuickRoomRestart
{
    internal static readonly FieldInfo? RunLobbyBackingField = typeof(RunManager).GetField("<RunLobby>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static bool _restartInProgress;
    internal static bool _liveRunCleaned;
    [ThreadStatic]
    internal static int _networkPreservingCleanupDepth;
    internal static SerializableRun? _preparedMultiplayerSave;
    internal static RunState? _preparedMultiplayerState;
    internal static ulong _preparedMultiplayerHostId;
    internal static bool CanDirectReload => RunLobbyBackingField != null;
    internal static bool IsNetworkPreservingCleanup => _networkPreservingCleanupDepth > 0;

    internal static bool CanRerollCombat
    {
        get
        {
            try
            {
                return CanRestart(out _) && RoomSaveTransform.TryGetActiveCombat(out _, out _);
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool CanRestart(out bool multiplayer)
    {
        multiplayer = false;
        try
        {
            var manager = RunManager.Instance;
            if (manager == null || !manager.IsInProgress)
                return false;
            multiplayer = !manager.IsSingleplayerOrFakeMultiplayer;
            if (!multiplayer)
                return SaveManager.Instance?.HasRunSave == true;
            return manager.NetService is INetHostGameService && SaveManager.Instance?.HasMultiplayerRunSave == true;
        }
        catch
        {
            return false;
        }
    }

    public static void Begin() => Begin(RestartMode.Exact);
    internal static void Begin(RestartMode mode)
    {
        if (_restartInProgress)
            return;
        _restartInProgress = true;
        _liveRunCleaned = false;
        if (CanRestart(out bool multiplayer) && multiplayer)
            _ = PrepareMultiplayerRestartAsync(mode);
        else
            _ = RestartAsync(mode);
    }

    internal static void ContinueAfterPreparation()
    {
        var save = _preparedMultiplayerSave;
        ulong hostId = _preparedMultiplayerHostId;
        _preparedMultiplayerSave = null;
        _preparedMultiplayerState = null;
        _preparedMultiplayerHostId = 0;
        if (save == null || hostId == 0)
        {
            ShowError("多人续局存档尚未准备完成，已取消本次重打。", resetGuard: true);
            return;
        }

        _ = RoomReload.RestartPreparedMultiplayerSafelyAsync(save, hostId);
    }

    internal static void ContinueDirectAfterPreparation()
    {
        var save = _preparedMultiplayerSave;
        var state = _preparedMultiplayerState;
        _preparedMultiplayerSave = null;
        _preparedMultiplayerState = null;
        _preparedMultiplayerHostId = 0;
        if (save == null || state == null)
        {
            ShowError("多人直载存档尚未准备完成，已取消本次重打。", resetGuard: true);
            return;
        }

        _ = RoomReload.RestartPreparedDirectMultiplayerSafelyAsync(save, state);
    }

    internal static void BeginDirectClientRestart(SerializableRun save, RunState state)
    {
        _liveRunCleaned = false;
        _ = RoomReload.RestartPreparedDirectMultiplayerSafelyAsync(save, state);
    }

    internal static void CancelPreparation()
    {
        _restartInProgress = false;
        _liveRunCleaned = false;
        _preparedMultiplayerSave = null;
        _preparedMultiplayerState = null;
        _preparedMultiplayerHostId = 0;
    }

    internal static async Task PrepareMultiplayerRestartAsync(RestartMode mode)
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

            // Validate before negotiating. Once a direct commit is sent the
            // old run will be torn down on every peer and cannot be recovered.
            RunState directState;
            try
            {
                RoomSaveTransform.PrepareLoadedSave(read.SaveData, mode);
                directState = RunState.FromSerializable(read.SaveData);
            }
            catch (Exception ex)
            {
                ShowError($"多人重打准备失败：{ex.Message}", resetGuard: true);
                return;
            }

            _preparedMultiplayerSave = read.SaveData;
            _preparedMultiplayerState = directState;
            _preparedMultiplayerHostId = hostId;
            GD.Print($"{RetryMod.LogPrefix}quick restart: multiplayer room-entry save prepared before handshake");
            MultiplayerRestartCoordinator.BeginHostPreparation(read.SaveData);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}quick restart preflight failed: {ex.Message}\n{ex.StackTrace}");
            ShowError($"重打准备失败：{ex.Message}", resetGuard: true);
        }
    }

    internal static async Task RestartAsync(RestartMode mode)
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
                try
                {
                    RoomSaveTransform.PrepareLoadedSave(read.SaveData, mode);
                    restored = RunState.FromSerializable(read.SaveData);
                }
                catch (Exception ex)
                {
                    ShowError($"重打准备失败：{ex.Message}", resetGuard: true);
                    return;
                }

                GD.Print($"{RetryMod.LogPrefix}quick restart: single-player room-entry save loaded");
                await RoomReload.RestartSingleplayerAsync(restored, read.SaveData);
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
                await RoomReload.EnsureMainMenuAfterFailureAsync();
                ShowError("重打加载失败，但本关入口存档仍然保留。请从主菜单继续游戏。", resetGuard: true);
            }
            else
            {
                ShowError($"重打加载失败：{ex.Message}", resetGuard: true);
            }
        }
    }

    internal static void ShowError(string body, bool resetGuard)
    {
        GD.PrintErr($"{RetryMod.LogPrefix}quick restart: {body}");
        if (resetGuard)
        {
            if (!_liveRunCleaned)
                MultiplayerRestartCoordinator.AbortBeforeDisconnect();
            _restartInProgress = false;
            _preparedMultiplayerSave = null;
            _preparedMultiplayerState = null;
            _preparedMultiplayerHostId = 0;
        }

        OneButtonNotice.Show("重打", body, "知道了", () =>
        {
        });
    }
}
