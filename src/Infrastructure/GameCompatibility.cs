using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Retry;

// Keep references to beta-only members out of IL compiled against the main branch.
internal static class GameCompatibility
{
    private static readonly PropertyInfo? HistoryCustom = typeof(RunHistory).GetProperty("IsCustomGame");
    private static readonly PropertyInfo? SaveCustom = typeof(SerializableRun).GetProperty("IsCustomGame");
    private static readonly PropertyInfo? StateCustom = typeof(RunState).GetProperty("IsCustomGame");
    private static readonly MethodInfo CreateRun = typeof(RunState).GetMethods().Single(m => m.Name == "CreateForNewRun");
    private static readonly MethodInfo StartRun = typeof(NGame).GetMethods().Single(m => m.Name == "StartNewSingleplayerRun");
    private static readonly ConstructorInfo AscensionConstructor = typeof(AscensionManager).GetConstructor(new[] { typeof(GameMode), typeof(int) }) ?? typeof(AscensionManager).GetConstructor(new[] { typeof(int) })!;
    private static readonly ConstructorInfo? BlitzMap = typeof(ActMap).Assembly.GetType("MegaCrit.Sts2.Core.Map.BlitzActMap")?.GetConstructor(new[] { typeof(SerializableActMap), typeof(IRunState) });
    private static readonly ConstructorInfo? NewBlitzMap = typeof(ActMap).Assembly.GetType("MegaCrit.Sts2.Core.Map.BlitzActMap")?.GetConstructor(new[] { typeof(IRunState) });

    internal static bool IsCustom(RunHistory history) => HistoryCustom?.GetValue(history) is true || history.GameMode.ToString() == "Custom";
    internal static bool IsCustom(SerializableRun save) => SaveCustom?.GetValue(save) is true || save.GameMode.ToString() == "Custom";
    internal static void SetCustom(SerializableRun save, bool value) => SaveCustom?.SetValue(save, value);
    internal static bool IsCustom(RunState state) => StateCustom?.GetValue(state) is true || state.GameMode.ToString() == "Custom";

    internal static GameMode RetryMode(GameMode mode, bool custom)
    {
        if (!Enum.IsDefined(typeof(GameMode), mode) || mode.ToString() == "None")
            throw new NotSupportedException($"Unsupported historical game mode: {mode}");
        // Daily replays must never be submitted as a fresh leaderboard attempt.
        if (mode.ToString() == "Daily")
            return Enum.TryParse<GameMode>("Custom", out var dailyFallback) ? dailyFallback : GameMode.Standard;
        if (custom && Enum.TryParse<GameMode>("Custom", out var customMode))
            return customMode;
        return mode;
    }

    internal static RunState CreateForNewRun(IReadOnlyList<Player> players, IReadOnlyList<ActModel> acts, IReadOnlyList<ModifierModel> modifiers, GameMode mode, int ascension, string seed, bool custom = false)
    {
        custom |= modifiers.Count != 0 || mode.ToString() == "Daily";
        mode = RetryMode(mode, custom);
        object?[] args = CreateRun.GetParameters().Length switch
        {
            6 => new object?[] { players, acts, modifiers, mode, ascension, seed },
            7 => new object?[] { players, acts, modifiers, mode, custom, ascension, seed },
            _ => throw new MissingMethodException("Unsupported RunState.CreateForNewRun signature")
        };
        return (RunState)CreateRun.Invoke(null, args)!;
    }

    internal static Task<RunState> StartSingleplayer(CharacterModel character, IReadOnlyList<ActModel> acts, IReadOnlyList<ModifierModel> modifiers, RetryTarget target)
    {
        bool custom = target.IsCustomGame || target.Modifiers.Count != 0 || target.GameMode.ToString() == "Daily";
        var mode = RetryMode(target.GameMode, custom);
        object?[] args;
        if (StartRun.GetParameters().Length == 8 && StartRun.GetParameters()[0].ParameterType == typeof(CharacterModel))
            args = new object?[] { character, true, acts, modifiers, target.Seed, mode, target.Ascension, null };
        else if (StartRun.GetParameters().Length == 9 && StartRun.GetParameters()[0].ParameterType == typeof(Player))
        {
            var player = Player.CreateForNewRun(character, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1UL);
            args = new object?[] { player, true, acts, modifiers, target.Seed, mode, custom, target.Ascension, null };
        }
        else
            throw new MissingMethodException("Unsupported NGame.StartNewSingleplayerRun signature");
        return (Task<RunState>)StartRun.Invoke(NGame.Instance, args)!;
    }

    internal static ActMap RestoreMap(SerializableActMap map, RunState? state)
    {
        if (state?.GameMode.ToString() == "Blitz")
            return (ActMap)(BlitzMap ?? throw new MissingMethodException("BlitzActMap constructor unavailable")).Invoke(new object[] { map, state });
        return new SavedActMap(map);
    }

    internal static ActMap CreateMap(RunState state) => state.GameMode.ToString() == "Blitz"
        ? (ActMap)(NewBlitzMap ?? throw new MissingMethodException("BlitzActMap constructor unavailable")).Invoke(new object[] { state })
        : state.Act.CreateMap(state, replaceTreasureWithElites: false);

    internal static void PrepareHistoryMode(SerializableRun save)
    {
        bool custom = IsCustom(save) || save.Modifiers.Count != 0 || save.GameMode.ToString() == "Daily";
        save.GameMode = RetryMode(save.GameMode, custom);
        SetCustom(save, custom);
        save.DailyTime = null;
    }

    internal static Dictionary<RunRngType, int> Counters(SerializableRunRngSet rng) => rng.Rngs.ToDictionary(kv => kv.Key, kv => kv.Value.counter);
    internal static AscensionManager Ascension(GameMode mode, int level) => (AscensionManager)AscensionConstructor.Invoke(AscensionConstructor.GetParameters().Length == 2 ? new object[] { mode, level } : new object[] { level });
}
