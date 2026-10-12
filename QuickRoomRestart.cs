using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.addons.mega_text;

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
    private static readonly FieldInfo? RunLobbyBackingField = typeof(RunManager).GetField(
        "<RunLobby>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

    private static bool _restartInProgress;
    private static bool _liveRunCleaned;
    [ThreadStatic]
    private static int _networkPreservingCleanupDepth;
    private static SerializableRun? _preparedMultiplayerSave;
    private static RunState? _preparedMultiplayerState;
    private static ulong _preparedMultiplayerHostId;
    private static readonly Dictionary<string, int> LastShuffleCounters = new();

    internal static bool CanDirectReload => RunLobbyBackingField != null;

    internal static bool IsNetworkPreservingCleanup
        => _networkPreservingCleanupDepth > 0;

    internal static bool CanRerollCombat
    {
        get
        {
            try
            {
                return CanRestart(out _) && TryGetActiveCombat(out _, out _);
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
        => Begin(RestartMode.Exact);

    internal static void Begin(RestartMode mode)
    {
        if (_restartInProgress) return;
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
        _ = RestartPreparedMultiplayerSafelyAsync(save, hostId);
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
        _ = RestartPreparedDirectMultiplayerSafelyAsync(save, state);
    }

    internal static void BeginDirectClientRestart(SerializableRun save, RunState state)
    {
        _liveRunCleaned = false;
        _ = RestartPreparedDirectMultiplayerSafelyAsync(save, state);
    }

    internal static void CancelPreparation()
    {
        _restartInProgress = false;
        _liveRunCleaned = false;
        _preparedMultiplayerSave = null;
        _preparedMultiplayerState = null;
        _preparedMultiplayerHostId = 0;
    }

    private static async Task PrepareMultiplayerRestartAsync(RestartMode mode)
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
                PrepareLoadedSave(read.SaveData, mode);
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

    private static async Task RestartAsync(RestartMode mode)
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
                    PrepareLoadedSave(read.SaveData, mode);
                    restored = RunState.FromSerializable(read.SaveData);
                }
                catch (Exception ex)
                {
                    ShowError($"重打准备失败：{ex.Message}", resetGuard: true);
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

    private static void PrepareLoadedSave(SerializableRun save, RestartMode mode)
    {
        bool hasActiveCombat = TryGetActiveCombat(out var liveState, out var currentEncounter);
        if (mode != RestartMode.Exact && !hasActiveCombat)
            throw new InvalidOperationException("只有正在进行的战斗可以使用重掷功能。");

        // A previous encounter reroll may only exist in the active in-memory
        // run. Mirror the live encounter into the room queue before applying
        // the next operation so a shuffle reroll or exact restart cannot
        // silently restore the original enemy. PreFinishedRoom is optional in
        // native combat-entry saves; the queue is the authoritative fallback.
        if (hasActiveCombat)
        {
            SetSavedEncounter(save, currentEncounter!, currentEncounter!);
            PreserveLastShuffleCounter(save, liveState!);
        }

        switch (mode)
        {
            case RestartMode.Exact:
                return;
            case RestartMode.RerollEncounter:
                RerollEncounter(save, liveState!, currentEncounter!);
                return;
            case RestartMode.RerollShuffle:
                RerollShuffle(save, liveState!);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    private static void RerollEncounter(
        SerializableRun save,
        RunState liveState,
        EncounterModel current)
    {
        IEnumerable<EncounterModel> pool = current.RoomType switch
        {
            RoomType.Monster when current.IsWeak => liveState.Act.AllWeakEncounters,
            RoomType.Monster => liveState.Act.AllRegularEncounters,
            RoomType.Elite => liveState.Act.AllEliteEncounters,
            RoomType.Boss => liveState.Act.AllBossEncounters,
            _ => Array.Empty<EncounterModel>(),
        };

        var candidates = pool
            .Where(encounter => !encounter.IsDebugEncounter && encounter.Id != current.Id)
            .GroupBy(encounter => encounter.Id)
            .Select(group => group.First())
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException("当前章节没有其他同层级遭遇可供重掷。");

        var selected = candidates[
            System.Security.Cryptography.RandomNumberGenerator.GetInt32(candidates.Count)];
        SetSavedEncounter(save, current, selected);
        GD.Print(
            $"{RetryMod.LogPrefix}encounter reroll: {current.Id} -> {selected.Id} " +
            $"act={liveState.CurrentActIndex} type={current.RoomType} weak={current.IsWeak}");
    }

    private static bool TryGetActiveCombat(
        out RunState? liveState,
        out EncounterModel? encounter)
    {
        liveState = null;
        encounter = null;
        try
        {
            var runManager = RunManager.Instance;
            var combatManager = CombatManager.Instance;
            if (runManager == null || !runManager.IsInProgress
                || combatManager == null || !combatManager.IsInProgress)
                return false;

            var combatState = combatManager.DebugOnlyGetState();
            if (combatState == null) return false;
            liveState = combatState.RunState as RunState
                ?? StateAccessor.GetState(runManager);
            encounter = combatState.Encounter;
            return liveState != null && encounter != null;
        }
        catch
        {
            liveState = null;
            encounter = null;
            return false;
        }
    }

    private static void SetSavedEncounter(
        SerializableRun save,
        EncounterModel current,
        EncounterModel replacement)
    {
        if (save.Acts == null
            || save.CurrentActIndex < 0
            || save.CurrentActIndex >= save.Acts.Count)
            throw new InvalidOperationException("房间入口存档的章节索引无效。");

        var rooms = save.Acts[save.CurrentActIndex].SerializableRooms
            ?? throw new InvalidOperationException("房间入口存档缺少本章节的遭遇队列。");

        switch (current.RoomType)
        {
            case RoomType.Monster:
                ReplaceQueuedEncounter(
                    rooms.NormalEncounterIds,
                    rooms.NormalEncountersVisited,
                    replacement.Id,
                    "普通战斗");
                break;
            case RoomType.Elite:
                ReplaceQueuedEncounter(
                    rooms.EliteEncounterIds,
                    rooms.EliteEncountersVisited,
                    replacement.Id,
                    "精英战斗");
                break;
            case RoomType.Boss:
                if (current.Id == rooms.BossId)
                    rooms.BossId = replacement.Id;
                else if (current.Id == rooms.SecondBossId)
                    rooms.SecondBossId = replacement.Id;
                else if (rooms.BossEncountersVisited <= 0)
                    rooms.BossId = replacement.Id;
                else
                    rooms.SecondBossId = replacement.Id;
                break;
            default:
                throw new InvalidOperationException(
                    $"当前遭遇类型 {current.RoomType} 不支持重打同步。");
        }

        var savedRoom = save.PreFinishedRoom;
        if (savedRoom != null && savedRoom.EncounterId != replacement.Id)
        {
            savedRoom.EncounterId = replacement.Id;
            savedRoom.EncounterState.Clear();
        }
    }

    private static void ReplaceQueuedEncounter(
        List<ModelId> encounterIds,
        int encountersVisited,
        ModelId replacementId,
        string queueName)
    {
        if (encounterIds == null || encounterIds.Count == 0)
            throw new InvalidOperationException($"房间入口存档的{queueName}队列为空。");
        if (encountersVisited < 0)
            throw new InvalidOperationException($"房间入口存档的{queueName}计数无效。");

        int index = encountersVisited % encounterIds.Count;
        encounterIds[index] = replacementId;
    }

    private static void RerollShuffle(SerializableRun save, RunState liveState)
    {
        var rng = save.SerializableRng
            ?? throw new InvalidOperationException("房间入口存档缺少随机数状态。");
        rng.Counters.TryGetValue(RunRngType.Shuffle, out int savedCounter);

        string key = ShuffleCounterKey(save, liveState);
        int lastCounter = LastShuffleCounters.TryGetValue(key, out int used)
            ? used
            : savedCounter;
        int baseCounter = Math.Max(savedCounter, lastCounter);
        if (baseCounter == int.MaxValue)
            throw new InvalidOperationException("洗牌随机数计数已达到上限。");

        int nextCounter = baseCounter + 1;
        rng.Counters[RunRngType.Shuffle] = nextCounter;
        LastShuffleCounters[key] = nextCounter;
        GD.Print(
            $"{RetryMod.LogPrefix}shuffle reroll: {savedCounter} -> {nextCounter} key={key}");
    }

    private static void PreserveLastShuffleCounter(SerializableRun save, RunState liveState)
    {
        var rng = save.SerializableRng;
        if (rng == null) return;

        string key = ShuffleCounterKey(save, liveState);
        if (!LastShuffleCounters.TryGetValue(key, out int lastCounter)) return;
        rng.Counters.TryGetValue(RunRngType.Shuffle, out int savedCounter);
        if (lastCounter > savedCounter)
            rng.Counters[RunRngType.Shuffle] = lastCounter;
    }

    private static string ShuffleCounterKey(SerializableRun save, RunState liveState)
    {
        var location = liveState.MapLocation;
        string coord = location.coord is { } value
            ? $"{value.row},{value.col}"
            : "none";
        return $"{save.StartTime}|{location.actIndex}|{coord}";
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
        // Split combat and network cleanup deliberately. A graceful Steam host
        // shutdown delays closing its listen socket; starting the replacement
        // host during that delay lets the old cleanup close the new listener.
        // RunManager.CleanUp(false), however, skips CombatManager.Reset and
        // leaves the old combat state installed, so a combat-room reload can
        // fail in SetUpCombat and tear down the newly created room.
        CombatManager.Instance.Reset(graceful: true);
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

    private static async Task RestartPreparedDirectMultiplayerSafelyAsync(
        SerializableRun save,
        RunState restored)
    {
        try
        {
            await RestartPreparedDirectMultiplayerAsync(save, restored);
            MultiplayerRestartCoordinator.DirectLoadCompleted();
            _restartInProgress = false;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}quick restart direct load failed: {ex.Message}\n{ex.StackTrace}");
            MultiplayerRestartCoordinator.DirectLoadFailed();
            _restartInProgress = false;
            _preparedMultiplayerSave = null;
            _preparedMultiplayerState = null;
            _preparedMultiplayerHostId = 0;

            try
            {
                var manager = RunManager.Instance;
                var net = manager?.NetService;
                if (manager?.IsInProgress == true)
                    manager.CleanUp(graceful: false);
                if (net?.IsConnected == true)
                    net.Disconnect(NetError.Quit, false);
            }
            catch { }

            await EnsureMainMenuAfterFailureAsync();
            OneButtonNotice.Show(
                "重打",
                "多人直载失败，当前联机连接已关闭。入口存档仍然保留，请从主菜单继续游戏。",
                "知道了",
                () => { });
        }
    }

    private static async Task RestartPreparedDirectMultiplayerAsync(
        SerializableRun save,
        RunState restored)
    {
        var game = NGame.Instance ?? throw new InvalidOperationException("NGame.Instance is unavailable");
        var manager = RunManager.Instance ?? throw new InvalidOperationException("RunManager.Instance is unavailable");
        var net = manager.NetService ?? throw new InvalidOperationException("multiplayer service is unavailable");
        var oldLobby = manager.RunLobby ?? throw new InvalidOperationException("active run lobby is unavailable");
        if (!net.IsConnected)
            throw new InvalidOperationException("multiplayer service disconnected before direct reload");
        if (RunLobbyBackingField == null)
            throw new MissingFieldException(typeof(RunManager).FullName, "<RunLobby>k__BackingField");

        try { await game.Transition.FadeOut(0.2f); } catch { }

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
        RunLobbyBackingField.SetValue(manager, null);
        CleanUpRunPreservingNetwork(manager);
        _liveRunCleaned = true;
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
                try { loadLobby.CleanUp(true, NetError.Quit); }
                catch { }
            }
        }

        try { await game.Transition.FadeIn(0.2f); } catch { }
        GD.Print(
            $"{RetryMod.LogPrefix}quick restart: multiplayer direct reload complete " +
            $"lobby={SafeLobbyIdentifier(net) ?? "?"}");
    }

    private static void CleanUpRunPreservingNetwork(RunManager manager)
    {
        _networkPreservingCleanupDepth++;
        try
        {
            manager.CleanUp(graceful: false);
        }
        finally
        {
            _networkPreservingCleanupDepth--;
        }
    }

    private static string? SafeLobbyIdentifier(INetGameService net)
    {
        try { return net.GetRawLobbyIdentifier(); }
        catch { return null; }
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
            _preparedMultiplayerState = null;
            _preparedMultiplayerHostId = 0;
        }
        OneButtonNotice.Show("重打", body, "知道了", () => { });
    }
}

[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.Disconnect),
    new[] { typeof(NetError), typeof(bool) })]
internal static class DirectRestartClientDisconnectPatch
{
    private static bool Prefix(NetError reason, bool now)
        => AllowOrSuppressDisconnect("client", reason, now);

    private static bool AllowOrSuppressDisconnect(string side, NetError reason, bool now)
    {
        if (!QuickRoomRestart.IsNetworkPreservingCleanup) return true;
        GD.Print(
            $"{RetryMod.LogPrefix}quick restart: suppressed {side} disconnect " +
            $"during direct cleanup reason={reason} now={now}");
        return false;
    }
}

[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.Disconnect),
    new[] { typeof(NetError), typeof(bool) })]
internal static class DirectRestartHostDisconnectPatch
{
    private static bool Prefix(NetError reason, bool now)
    {
        if (!QuickRoomRestart.IsNetworkPreservingCleanup) return true;
        GD.Print(
            $"{RetryMod.LogPrefix}quick restart: suppressed host disconnect " +
            $"during direct cleanup reason={reason} now={now}");
        return false;
    }
}

internal sealed class DirectLoadRunLobbyListener : ILoadRunLobbyListener
{
    internal static DirectLoadRunLobbyListener Instance { get; } = new();

    public void BeginRun() { }
    public void LocalPlayerDisconnected(NetErrorInfo info) { }
    public void PlayerConnected(ulong playerId) { }
    public void PlayerReadyChanged(ulong playerId) { }
    public void RemotePlayerDisconnected(ulong playerId) { }
    public Task<bool> ShouldAllowRunToBegin() => Task.FromResult(true);
}

[HarmonyPatch(typeof(NPauseMenu), "_Ready")]
public static class NPauseMenu_QuickRestart_Patch
{
    private const string RestartButtonName = "RetryTheSpireQuickRoomRestartButton";
    private const string EncounterButtonName = "RetryTheSpireRerollEncounterButton";
    private const string ShuffleButtonName = "RetryTheSpireRerollShuffleButton";

    private static readonly FieldInfo? ResumeButtonField = typeof(NPauseMenu).GetField(
        "_resumeButton", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? SettingsButtonField = typeof(NPauseMenu).GetField(
        "_settingsButton", BindingFlags.Instance | BindingFlags.NonPublic);

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
        if (!RetryMod.Enabled) return;
        try
        {
            if (__instance.FindChild(RestartButtonName, recursive: true, owned: false) != null) return;

            var resume = ResumeButtonField?.GetValue(__instance) as NPauseMenuButton;
            var settings = SettingsButtonField?.GetValue(__instance) as NPauseMenuButton;
            if (resume == null || settings == null) return;

            const int duplicateFlags = (int)(Node.DuplicateFlags.Groups
                | Node.DuplicateFlags.Scripts
                | Node.DuplicateFlags.UseInstantiation);
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
                button.Connect(
                    NClickableControl.SignalName.Released,
                    Callable.From<NButton>(_ => QuickRoomRestart.Begin(mode)));
                buttons.Add(button);
            }

            var parent = resume.GetParent();
            if (parent == null) return;
            var settingsPosition = settings.Position;
            var originalPositions = parent.GetChildren()
                .OfType<NPauseMenuButton>()
                .ToDictionary(button => button, button => button.Position);
            foreach (var button in buttons) parent.AddChild(button);

            if (parent is Container)
            {
                int insertAt = resume.GetIndex() + 1;
                for (int i = 0; i < buttons.Count; i++)
                    parent.MoveChild(buttons[i], Math.Min(insertAt + i, parent.GetChildCount() - 1));
            }

            float spacing = settingsPosition.Y - resume.Position.Y;
            if (spacing < 4f) spacing = Math.Max(resume.Size.Y + 20f, 90f);
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
            __instance.Connect(
                CanvasItem.SignalName.VisibilityChanged,
                Callable.From(() => RefreshButtons(state)));
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
        if (!GodotObject.IsInstanceValid(state.Menu)) return;

        bool canRestart = QuickRoomRestart.CanRestart(out _);
        bool canReroll = canRestart && QuickRoomRestart.CanRerollCombat;
        SetVisibleAndFocusable(state.Restart, canRestart, state.VisibleFocusMode);
        SetVisibleAndFocusable(state.Encounter, canReroll, state.VisibleFocusMode);
        SetVisibleAndFocusable(state.Shuffle, canReroll, state.VisibleFocusMode);

        var visibleButtons = new List<NPauseMenuButton>(3);
        if (state.Restart.Visible) visibleButtons.Add(state.Restart);
        if (state.Encounter.Visible) visibleButtons.Add(state.Encounter);
        if (state.Shuffle.Visible) visibleButtons.Add(state.Shuffle);

        if (state.Parent is not Container)
        {
            foreach (var (button, originalPosition) in state.OriginalPositions)
            {
                if (button == state.Resume) continue;
                button.Position = originalPosition.Y >= state.SettingsPosition.Y - 1f
                    ? originalPosition + new Vector2(0f, state.Spacing * visibleButtons.Count)
                    : originalPosition;
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

        GD.Print(
            $"{RetryMod.LogPrefix}pause restart buttons refreshed " +
            $"restart={canRestart} rerolls={canReroll}");
    }

    private static void SetVisibleAndFocusable(
        NPauseMenuButton button,
        bool visible,
        Control.FocusModeEnum visibleFocusMode)
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
        foreach (Node child in root.GetChildren()) SetButtonText(child, text);
    }

    private static void CloneMaterials(Node root)
    {
        if (root is CanvasItem item && item.Material != null)
            item.Material = (Material)item.Material.Duplicate();
        foreach (Node child in root.GetChildren()) CloneMaterials(child);
    }
}
