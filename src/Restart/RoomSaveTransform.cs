using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Retry;

internal static class RoomSaveTransform
{
    internal static readonly Dictionary<string, int> LastShuffleCounters = new();
    internal static void PrepareLoadedSave(SerializableRun save, RestartMode mode)
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

    internal static void RerollEncounter(SerializableRun save, RunState liveState, EncounterModel current)
    {
        IEnumerable<EncounterModel> pool = current.RoomType switch
        {
            RoomType.Monster when current.IsWeak => liveState.Act.AllWeakEncounters,
            RoomType.Monster => liveState.Act.AllRegularEncounters,
            RoomType.Elite => liveState.Act.AllEliteEncounters,
            RoomType.Boss => liveState.Act.AllBossEncounters,
            _ => Array.Empty<EncounterModel>(),
        };
        var candidates = pool.Where(encounter => !encounter.IsDebugEncounter && encounter.Id != current.Id).GroupBy(encounter => encounter.Id).Select(group => group.First()).ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException("当前章节没有其他同层级遭遇可供重掷。");
        var selected = candidates[System.Security.Cryptography.RandomNumberGenerator.GetInt32(candidates.Count)];
        SetSavedEncounter(save, current, selected);
        GD.Print($"{RetryMod.LogPrefix}encounter reroll: {current.Id} -> {selected.Id} " + $"act={liveState.CurrentActIndex} type={current.RoomType} weak={current.IsWeak}");
    }

    internal static bool TryGetActiveCombat(out RunState? liveState, out EncounterModel? encounter)
    {
        liveState = null;
        encounter = null;
        try
        {
            var runManager = RunManager.Instance;
            var combatManager = CombatManager.Instance;
            if (runManager == null || !runManager.IsInProgress || combatManager == null || !combatManager.IsInProgress)
                return false;
            var combatState = combatManager.DebugOnlyGetState();
            if (combatState == null)
                return false;
            liveState = combatState.RunState as RunState ?? StateAccessor.GetState(runManager);
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

    internal static void SetSavedEncounter(SerializableRun save, EncounterModel current, EncounterModel replacement)
    {
        if (save.Acts == null || save.CurrentActIndex < 0 || save.CurrentActIndex >= save.Acts.Count)
            throw new InvalidOperationException("房间入口存档的章节索引无效。");
        var rooms = save.Acts[save.CurrentActIndex].SerializableRooms ?? throw new InvalidOperationException("房间入口存档缺少本章节的遭遇队列。");
        switch (current.RoomType)
        {
            case RoomType.Monster:
                ReplaceQueuedEncounter(rooms.NormalEncounterIds, rooms.NormalEncountersVisited, replacement.Id, "普通战斗");
                break;
            case RoomType.Elite:
                ReplaceQueuedEncounter(rooms.EliteEncounterIds, rooms.EliteEncountersVisited, replacement.Id, "精英战斗");
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
                throw new InvalidOperationException($"当前遭遇类型 {current.RoomType} 不支持重打同步。");
        }

        var savedRoom = save.PreFinishedRoom;
        if (savedRoom != null && savedRoom.EncounterId != replacement.Id)
        {
            savedRoom.EncounterId = replacement.Id;
            savedRoom.EncounterState.Clear();
        }
    }

    internal static void ReplaceQueuedEncounter(List<ModelId> encounterIds, int encountersVisited, ModelId replacementId, string queueName)
    {
        if (encounterIds == null || encounterIds.Count == 0)
            throw new InvalidOperationException($"房间入口存档的{queueName}队列为空。");
        if (encountersVisited < 0)
            throw new InvalidOperationException($"房间入口存档的{queueName}计数无效。");
        int index = encountersVisited % encounterIds.Count;
        encounterIds[index] = replacementId;
    }

    internal static void RerollShuffle(SerializableRun save, RunState liveState)
    {
        var rng = save.SerializableRng ?? throw new InvalidOperationException("房间入口存档缺少随机数状态。");
        rng.Counters.TryGetValue(RunRngType.Shuffle, out int savedCounter);
        string key = ShuffleCounterKey(save, liveState);
        int lastCounter = LastShuffleCounters.TryGetValue(key, out int used) ? used : savedCounter;
        int baseCounter = Math.Max(savedCounter, lastCounter);
        if (baseCounter == int.MaxValue)
            throw new InvalidOperationException("洗牌随机数计数已达到上限。");
        int nextCounter = baseCounter + 1;
        rng.Counters[RunRngType.Shuffle] = nextCounter;
        LastShuffleCounters[key] = nextCounter;
        GD.Print($"{RetryMod.LogPrefix}shuffle reroll: {savedCounter} -> {nextCounter} key={key}");
    }

    internal static void PreserveLastShuffleCounter(SerializableRun save, RunState liveState)
    {
        var rng = save.SerializableRng;
        if (rng == null)
            return;
        string key = ShuffleCounterKey(save, liveState);
        if (!LastShuffleCounters.TryGetValue(key, out int lastCounter))
            return;
        rng.Counters.TryGetValue(RunRngType.Shuffle, out int savedCounter);
        if (lastCounter > savedCounter)
            rng.Counters[RunRngType.Shuffle] = lastCounter;
    }

    internal static string ShuffleCounterKey(SerializableRun save, RunState liveState)
    {
        var location = liveState.MapLocation;
        string coord = location.coord is { } value ? $"{value.row},{value.col}" : "none";
        return $"{save.StartTime}|{location.actIndex}|{coord}";
    }
}
