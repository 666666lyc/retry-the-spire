// Builds a native multiplayer SerializableRun at the boundary immediately
// before the selected historical room, then hands it to the game's own
// LoadRunLobby. The serialized payload contains no Retry-owned types, so
// joining clients do not need this mod installed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace Retry;

internal static class MultiplayerRetryLauncher
{
    private sealed class NativeSaveBuildResult
    {
        public required SerializableRun Save;
        public string? TargetRoomWarning;
    }

    private sealed class TargetRoomContract
    {
        public required MapPointType HistoricalPointType;
        public RoomType? HistoricalRoomType;
        public MapPointType? ExpectedSavedPointType;
        public bool UsesForcedUnknownOdds;
        public string? Warning;
    }

    private static readonly FieldInfo? MapHistoryField = typeof(RunState).GetField(
        "_mapPointHistory", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? VisitedCoordsField = typeof(RunState).GetField(
        "_visitedMapCoords", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Begin(
        RunHistory history,
        RunHistoryPlayer selectedPlayer,
        int actIndex,
        int floorIndex,
        MapCoord targetCoord,
        IReadOnlyList<MapCoord> pathThroughTarget,
        ulong hostNetId,
        NMainMenu? mainMenu,
        bool browserAlreadyClosed = false)
    {
        bool browserClosed = browserAlreadyClosed;
        try
        {
            ValidateRequest(
                history, actIndex, floorIndex, targetCoord,
                pathThroughTarget, hostNetId, mainMenu);

            var target = BuildTarget(history, selectedPlayer, actIndex, floorIndex);

            // The browser owns a fake single-player RunManager. Tear it down
            // before installing the clean synthesis state used for ToSave.
            if (!browserClosed)
            {
                NActMapBrowser.Close();
                browserClosed = true;
            }

            var build = BuildNativeSave(
                history, target, actIndex, floorIndex,
                targetCoord, pathThroughTarget, hostNetId);
            var save = build.Save;

            RetryContext.ResetAll();
            GD.Print(
                $"{RetryMod.LogPrefix}multiplayer native save ready: " +
                $"players={save.Players.Count} act={save.CurrentActIndex} " +
                $"visited={save.VisitedMapCoords.Count} history={save.MapPointHistory.Sum(a => a.Count)} " +
                $"mode={save.GameMode} ascension={save.Ascension}");

            // Let the browser overlay finish its queued frees before pushing
            // the multiplayer submenu and starting the Steam host.
            var tree = mainMenu!.GetTree();
            tree.CreateTimer(0.05).Connect("timeout", Callable.From(() =>
            {
                try
                {
                    RetryContext.ResetAll();
                    LocalContext.NetId = hostNetId;
                    void StartLobby()
                    {
                        var submenu = mainMenu.OpenMultiplayerSubmenu();
                        submenu.StartHost(save);
                        GD.Print($"{RetryMod.LogPrefix}native multiplayer load lobby requested");
                    }

                    if (!string.IsNullOrWhiteSpace(build.TargetRoomWarning))
                    {
                        OneButtonNotice.Show(
                            "房间类型还原警告",
                            build.TargetRoomWarning + "\n\n仍将继续创建房间。",
                            "继续开房",
                            StartLobby);
                    }
                    else
                    {
                        StartLobby();
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"{RetryMod.LogPrefix}start multiplayer host: {ex.Message}\n{ex.StackTrace}");
                }
            }));
        }
        catch (Exception ex)
        {
            RetryContext.ResetAll();
            GD.PrintErr($"{RetryMod.LogPrefix}multiplayer host failed: {ex.Message}\n{ex.StackTrace}");
            if (!browserClosed) NActMapBrowser.NotifyMultiplayerLaunchFailed();
        }
    }

    private static void ValidateRequest(
        RunHistory history,
        int actIndex,
        int floorIndex,
        MapCoord targetCoord,
        IReadOnlyList<MapCoord> pathThroughTarget,
        ulong hostNetId,
        NMainMenu? mainMenu)
    {
        if (mainMenu == null) throw new InvalidOperationException("main menu node was not found");
        if (history.GameMode != GameMode.Standard)
            throw new InvalidOperationException("only Standard history runs can open an ascension-eligible lobby");
        if (history.Modifiers.Count != 0)
            throw new InvalidOperationException("history run has modifiers and cannot be hosted as a normal Standard run");
        if (history.Players.Count < 2)
            throw new InvalidOperationException("history run is not multiplayer");
        if (history.Players.All(p => p.Id != hostNetId))
            throw new InvalidOperationException($"local Steam id {hostNetId} is not one of the historical players");
        if (actIndex < 0 || actIndex >= history.Acts.Count || actIndex >= history.MapPointHistory.Count)
            throw new ArgumentOutOfRangeException(nameof(actIndex));
        if (floorIndex < 0 || floorIndex >= history.MapPointHistory[actIndex].Count)
            throw new ArgumentOutOfRangeException(nameof(floorIndex));
        if (pathThroughTarget.Count == 0)
            throw new InvalidOperationException("historical path is empty");
        var last = pathThroughTarget[pathThroughTarget.Count - 1];
        if (last.row != targetCoord.row || last.col != targetCoord.col)
            throw new InvalidOperationException("historical path does not end at the selected node");

        var ids = new HashSet<ulong>();
        foreach (var hp in history.Players)
        {
            if (!ids.Add(hp.Id))
                throw new InvalidOperationException($"duplicate historical player id {hp.Id}");
            if (ModelDb.GetByIdOrNull<CharacterModel>(hp.Character) == null)
                throw new InvalidOperationException(
                    $"character {hp.Character} for player {hp.Id} is missing from the current ModelDb");
        }
    }

    private static RetryTarget BuildTarget(
        RunHistory history,
        RunHistoryPlayer selectedPlayer,
        int actIndex,
        int floorIndex)
    {
        var target = new RetryTarget
        {
            Seed = history.Seed,
            SourceStartTime = history.StartTime,
            Ascension = history.Ascension,
            GameMode = GameMode.Standard,
            ActIds = new List<ModelId>(history.Acts),
            Modifiers = new List<SerializableModifier>(),
            TargetActIndex = actIndex,
            TargetFloorIndex = floorIndex,
            MapPointHistorySoFar = TruncateHistoryIncludingTarget(history, actIndex, floorIndex),
            OriginalRunTime = history.RunTime,
            OriginalTotalFloors = history.MapPointHistory.Sum(a => a.Count),
        };

        foreach (var hp in history.Players)
        {
            var snapshot = StateReconstructor.ReconstructAtTarget(
                history, actIndex, floorIndex, hp);
            target.Players.Add(snapshot);
            if (hp.Id == selectedPlayer.Id) target.Player = snapshot;
        }

        if (target.Player.NetId == 0)
            target.Player = target.Players.First(p => p.NetId == selectedPlayer.Id);
        return target;
    }

    private static NativeSaveBuildResult BuildNativeSave(
        RunHistory history,
        RetryTarget target,
        int actIndex,
        int floorIndex,
        MapCoord targetCoord,
        IReadOnlyList<MapCoord> pathThroughTarget,
        ulong hostNetId)
    {
        RetryContext.ResetAll();

        var players = new List<Player>(history.Players.Count);
        foreach (var hp in history.Players)
        {
            var character = ModelDb.GetById<CharacterModel>(hp.Character);
            players.Add(Player.CreateForNewRun(character, UnlockState.all, hp.Id));
        }

        var acts = new List<ActModel>(history.Acts.Count);
        foreach (var id in history.Acts)
            acts.Add(ModelDb.GetById<ActModel>(id).ToMutable());

        var runState = RunState.CreateForNewRun(
            players,
            acts,
            new List<ModifierModel>(),
            GameMode.Standard,
            history.Ascension,
            history.Seed);
        runState.ExtraFields.StartedWithNeow = true;

        // Generate maps in historical order. Map RNG is shared, so later-act
        // maps are only identical when every preceding act consumed its map
        // generation stream first.
        for (int i = 0; i <= actIndex; i++)
        {
            runState.CurrentActIndex = i;
            var generated = runState.Acts[i].CreateMap(runState, replaceTreasureWithElites: false);
            if (i == actIndex) runState.Map = generated;
        }
        runState.CurrentActIndex = actIndex;
        runState.ActFloor = floorIndex;

        // Close() restored the real local id and removed the browser's fake
        // net service. Reinstall a no-save service only long enough for the
        // game's own ToSave implementation to serialize all native fields.
        NActMapBrowser.NetServiceNetIdOverride = hostNetId;
        LocalContext.NetId = hostNetId;
        try
        {
            RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: false);
            RunManager.Instance.Launch();

            foreach (var snapshot in target.Players)
                InventoryInjector.Apply(runState, snapshot, silent: true);

            PopulateCompletedHistory(runState, history, actIndex, floorIndex);
            ReplaceVisitedCoords(runState, pathThroughTarget);
            SeedEventsFromCompletedActs(runState, history, actIndex);

            // Reconstruct the current act's encounter/event counters up to,
            // but not including, the selected room.
            EventListPatcher.AlignToHistory(runState, target);
            RoomQueueSimulator.SimulatePriorRooms(runState, target);

            try
            {
                RngSnapshotStore.TryApplyLive(
                    runState.Rng, target.SourceStartTime, target.Seed, actIndex, floorIndex);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"{RetryMod.LogPrefix}multiplayer RNG snapshot: {ex.Message}");
            }

            var roomContract = ApplyTargetRoomContract(runState, target, targetCoord);

            RunTimeOffset.Apply(target);
            var save = RunManager.Instance.ToSave(preFinishedRoom: null);

            // Explicitly stamp the progression-safe contract after ToSave.
            save.GameMode = GameMode.Standard;
            save.Modifiers = new List<SerializableModifier>();
            save.Ascension = history.Ascension;
            save.CurrentActIndex = actIndex;
            save.PlatformType = history.PlatformType;

            // StartTime is the native run identity, not merely display
            // metadata. Reusing the source history's value makes the next
            // startup treat current_run_mp.save as stale because a history
            // file with that StartTime already exists, so the game deletes
            // the resumable multiplayer save. Give the retry its own identity
            // while RunTimeOffset continues to preserve the historical timer.
            save.StartTime = CreateRetryStartTime(history.StartTime);
            save.PreFinishedRoom = null;
            save.VisitedMapCoords = new List<MapCoord>(pathThroughTarget);

            for (int act = 0; act < actIndex && act < target.MapPointHistorySoFar.Count; act++)
            {
                RngSnapshotStore.CopyExactCoordinates(
                    target.SourceStartTime, save.StartTime, target.Seed,
                    act, target.MapPointHistorySoFar[act].Count);
            }
            RngSnapshotStore.CaptureCoordinates(
                save.StartTime, target.Seed, actIndex, pathThroughTarget);

            ValidateNativeSave(save, history, targetCoord);
            var serializedWarning = ValidateSerializedTargetRoomContract(
                save, actIndex, targetCoord, roomContract);
            var warning = CombineWarnings(roomContract.Warning, serializedWarning);
            if (warning != null)
                GD.PrintErr($"{RetryMod.LogPrefix}multiplayer target room fallback: {warning}");

            return new NativeSaveBuildResult
            {
                Save = save,
                TargetRoomWarning = warning,
            };
        }
        finally
        {
            RetryContext.ResetAll();
            try { RunManager.Instance.CleanUp(graceful: false); }
            catch (Exception ex) { GD.PrintErr($"{RetryMod.LogPrefix}multiplayer synthesis cleanup: {ex.Message}"); }
            NActMapBrowser.NetServiceNetIdOverride = null;
            LocalContext.NetId = hostNetId;
        }
    }

    private static TargetRoomContract ApplyTargetRoomContract(
        RunState runState,
        RetryTarget target,
        MapCoord targetCoord)
    {
        var contract = new TargetRoomContract
        {
            HistoricalPointType = MapPointType.Unassigned,
        };

        if (target.MapPointHistorySoFar.Count <= target.TargetActIndex
            || target.MapPointHistorySoFar[target.TargetActIndex].Count <= target.TargetFloorIndex)
        {
            contract.Warning = "历史记录中缺少目标层信息，无法校验房间类型。";
            return contract;
        }

        var entry = target.MapPointHistorySoFar[target.TargetActIndex][target.TargetFloorIndex];
        contract.HistoricalPointType = entry.MapPointType;
        if (entry.Rooms != null && entry.Rooms.Count > 0)
            contract.HistoricalRoomType = entry.Rooms[0].RoomType;

        var map = runState.Map;
        var point = map?.GetAllMapPoints().FirstOrDefault(p => SameCoord(p.coord, targetCoord));
        if (point == null)
        {
            contract.Warning = $"生成地图中找不到目标坐标 ({targetCoord.row},{targetCoord.col})，无法固化历史房间类型。";
            return contract;
        }

        var generatedType = point.PointType;
        if (entry.MapPointType != MapPointType.Unknown)
        {
            point.PointType = entry.MapPointType;
            contract.ExpectedSavedPointType = entry.MapPointType;
            GD.Print(
                $"{RetryMod.LogPrefix}target room contract: " +
                $"coord=({targetCoord.row},{targetCoord.col}) historicalPoint={entry.MapPointType} " +
                $"historicalRoom={contract.HistoricalRoomType?.ToString() ?? "?"} " +
                $"generated={generatedType} final={point.PointType}");
            return contract;
        }

        contract.ExpectedSavedPointType = MapPointType.Unknown;
        if (!contract.HistoricalRoomType.HasValue)
        {
            contract.Warning = "历史问号节点没有记录实际房间类型，无法固定本次结果。";
            return contract;
        }

        point.PointType = MapPointType.Unknown;
        var desired = contract.HistoricalRoomType.Value;
        if (!TryForceUnknownOdds(runState, point, desired, out var reason))
        {
            contract.Warning = reason;
        }
        else
        {
            contract.UsesForcedUnknownOdds = true;
        }

        GD.Print(
            $"{RetryMod.LogPrefix}target room contract: " +
            $"coord=({targetCoord.row},{targetCoord.col}) historicalPoint=Unknown " +
            $"historicalRoom={desired} generated={generatedType} final={point.PointType} " +
            $"forcedOdds={contract.UsesForcedUnknownOdds}");
        return contract;
    }

    private static bool TryForceUnknownOdds(
        RunState runState,
        MapPoint point,
        RoomType desired,
        out string? reason)
    {
        reason = null;
        if (desired is not (RoomType.Event or RoomType.Monster or RoomType.Elite
            or RoomType.Treasure or RoomType.Shop))
        {
            reason = $"历史问号节点实际解析为 {desired}，当前游戏无法用问号房概率表达该类型。";
            return false;
        }

        try
        {
            MapPointHistoryEntry? previous = null;
            var history = runState.MapPointHistory;
            if (history.Count > runState.CurrentActIndex
                && history[runState.CurrentActIndex].Count > 0)
            {
                previous = history[runState.CurrentActIndex][history[runState.CurrentActIndex].Count - 1];
            }

            if (previous != null)
            {
                var blacklist = RunManager.BuildRoomTypeBlacklist(previous, point.Children);
                if (blacklist.Contains(desired))
                {
                    reason = $"历史房间类型 {desired} 被当前版本的问号房规则排除，无法保证还原。";
                    return false;
                }
            }

            var odds = runState.Odds.UnknownMapPoint;
            odds.MonsterOdds = desired == RoomType.Monster ? 1f : 0f;
            odds.EliteOdds = desired == RoomType.Elite ? 1f : 0f;
            odds.TreasureOdds = desired == RoomType.Treasure ? 1f : 0f;
            odds.ShopOdds = desired == RoomType.Shop ? 1f : 0f;

            bool deterministic = desired switch
            {
                RoomType.Event => odds.EventOdds >= 0.999f,
                RoomType.Monster => odds.MonsterOdds >= 0.999f,
                RoomType.Elite => odds.EliteOdds >= 0.999f,
                RoomType.Treasure => odds.TreasureOdds >= 0.999f,
                RoomType.Shop => odds.ShopOdds >= 0.999f,
                _ => false,
            };
            if (!deterministic)
            {
                reason = $"当前版本没有接受 {desired} 的确定性问号房概率。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = $"设置问号房概率时发生错误：{ex.Message}";
            return false;
        }
    }

    private static string? ValidateSerializedTargetRoomContract(
        SerializableRun save,
        int actIndex,
        MapCoord targetCoord,
        TargetRoomContract contract)
    {
        try
        {
            if (actIndex < 0 || actIndex >= save.Acts.Count)
                return "生成存档缺少目标幕，无法验证房间类型。";
            var savedMap = save.Acts[actIndex].SavedMap;
            if (savedMap == null)
                return "生成存档缺少目标地图，无法验证房间类型。";

            var savedPoint = savedMap.Points.FirstOrDefault(p => SameCoord(p.Coord, targetCoord));
            if (savedPoint == null && savedMap.StartingPoint != null
                && SameCoord(savedMap.StartingPoint.Coord, targetCoord))
                savedPoint = savedMap.StartingPoint;
            if (savedPoint == null && savedMap.BossPoint != null
                && SameCoord(savedMap.BossPoint.Coord, targetCoord))
                savedPoint = savedMap.BossPoint;
            if (savedPoint == null)
                return "生成存档中找不到目标地图点，无法验证房间类型。";

            GD.Print(
                $"{RetryMod.LogPrefix}serialized target room: " +
                $"coord=({targetCoord.row},{targetCoord.col}) point={savedPoint.PointType} " +
                $"historicalPoint={contract.HistoricalPointType} " +
                $"historicalRoom={contract.HistoricalRoomType?.ToString() ?? "?"}");

            if (contract.ExpectedSavedPointType.HasValue
                && savedPoint.PointType != contract.ExpectedSavedPointType.Value)
            {
                return $"目标地图点序列化为 {savedPoint.PointType}，历史要求为 {contract.ExpectedSavedPointType.Value}。";
            }

            if (contract.HistoricalPointType == MapPointType.Unknown
                && contract.UsesForcedUnknownOdds)
            {
                var odds = save.SerializableOdds;
                var desired = contract.HistoricalRoomType;
                bool valid = desired switch
                {
                    RoomType.Event => odds.UnknownMapPointMonsterOddsValue == 0f
                        && odds.UnknownMapPointEliteOddsValue == 0f
                        && odds.UnknownMapPointTreasureOddsValue == 0f
                        && odds.UnknownMapPointShopOddsValue == 0f,
                    RoomType.Monster => odds.UnknownMapPointMonsterOddsValue >= 0.999f,
                    RoomType.Elite => odds.UnknownMapPointEliteOddsValue >= 0.999f,
                    RoomType.Treasure => odds.UnknownMapPointTreasureOddsValue >= 0.999f,
                    RoomType.Shop => odds.UnknownMapPointShopOddsValue >= 0.999f,
                    _ => false,
                };
                if (!valid)
                    return $"目标问号房的 {desired} 概率未被正确写入存档。";
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"验证目标房间类型时发生错误：{ex.Message}";
        }
    }

    private static bool SameCoord(MapCoord a, MapCoord b)
        => a.row == b.row && a.col == b.col;

    private static string? CombineWarnings(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return string.IsNullOrWhiteSpace(second) ? null : second;
        if (string.IsNullOrWhiteSpace(second) || string.Equals(first, second, StringComparison.Ordinal)) return first;
        return first + "\n" + second;
    }

    private static void PopulateCompletedHistory(
        RunState runState,
        RunHistory history,
        int targetActIndex,
        int targetFloorIndex)
    {
        if (MapHistoryField?.GetValue(runState) is not List<List<MapPointHistoryEntry>> live)
            throw new InvalidOperationException("RunState._mapPointHistory was not found");

        live.Clear();
        for (int act = 0; act <= targetActIndex; act++)
        {
            var source = history.MapPointHistory[act];
            int take = act == targetActIndex ? targetFloorIndex : source.Count;
            live.Add(source.Take(take).ToList());
        }
    }

    private static void ReplaceVisitedCoords(
        RunState runState,
        IReadOnlyList<MapCoord> pathThroughTarget)
    {
        if (VisitedCoordsField?.GetValue(runState) is not List<MapCoord> live)
            throw new InvalidOperationException("RunState._visitedMapCoords was not found");
        live.Clear();
        live.AddRange(pathThroughTarget);
    }

    private static void SeedEventsFromCompletedActs(
        RunState runState,
        RunHistory history,
        int targetActIndex)
    {
        for (int act = 0; act < targetActIndex; act++)
        {
            foreach (var entry in history.MapPointHistory[act])
            {
                foreach (var room in entry.Rooms)
                {
                    if (room.RoomType != MegaCrit.Sts2.Core.Rooms.RoomType.Event || room.ModelId == null)
                        continue;
                    var model = ModelDb.GetByIdOrNull<EventModel>(room.ModelId);
                    if (model != null) runState.AddVisitedEvent(model);
                }
            }
        }
    }

    private static void ValidateNativeSave(
        SerializableRun save,
        RunHistory history,
        MapCoord targetCoord)
    {
        if (save.GameMode != GameMode.Standard || save.Modifiers.Count != 0)
            throw new InvalidOperationException("generated save is not an unmodified Standard run");
        if (save.PreFinishedRoom != null)
            throw new InvalidOperationException("generated save unexpectedly contains a pre-finished room");
        if (save.Players.Count != history.Players.Count)
            throw new InvalidOperationException("generated save lost one or more historical players");

        var expectedIds = history.Players.Select(p => p.Id).OrderBy(x => x).ToArray();
        var actualIds = save.Players.Select(p => p.NetId).OrderBy(x => x).ToArray();
        if (!expectedIds.SequenceEqual(actualIds))
            throw new InvalidOperationException("generated save player ids differ from the history record");
        if (save.StartTime == history.StartTime)
            throw new InvalidOperationException("generated save reused the source history run identity");
        if (save.VisitedMapCoords.Count == 0)
            throw new InvalidOperationException("generated save has no target coordinate");
        var last = save.VisitedMapCoords[save.VisitedMapCoords.Count - 1];
        if (last.row != targetCoord.row || last.col != targetCoord.col)
            throw new InvalidOperationException("generated save does not end at the selected coordinate");
    }

    private static long CreateRetryStartTime(long sourceStartTime)
    {
        long retryStartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return retryStartTime == sourceStartTime
            ? checked(retryStartTime + 1)
            : retryStartTime;
    }

    private static List<List<MapPointHistoryEntry>> TruncateHistoryIncludingTarget(
        RunHistory history,
        int targetActIndex,
        int targetFloorIndex)
    {
        var result = new List<List<MapPointHistoryEntry>>(targetActIndex + 1);
        for (int act = 0; act <= targetActIndex; act++)
        {
            var source = history.MapPointHistory[act];
            int take = act == targetActIndex ? targetFloorIndex + 1 : source.Count;
            result.Add(source.Take(take).ToList());
        }
        return result;
    }
}
